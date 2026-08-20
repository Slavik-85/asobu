using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Asobu.Core.Accounts;

public sealed class MicrosoftAuthException(string message) : Exception(message);

/// <summary>
/// Microsoft token to Xbox Live to XSTS to a Minecraft session. Shared by both sign-in routes,
/// because everything after the first hop is identical no matter how the Microsoft token was
/// obtained — only the shape of the ticket differs.
/// </summary>
public sealed class XboxChain(HttpClient http)
{
    private const string XboxAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string DeviceAuthUrl = "https://device.auth.xboxlive.com/device/authenticate";
    private const string SisuAuthorizeUrl = "https://sisu.xboxlive.com/authorize";
    private const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    /// <summary>Route for a token from our own Azure registration: user authenticate, then XSTS.</summary>
    public async Task<MinecraftSession> ExchangeAsync(
        string microsoftToken, CancellationToken cancellationToken = default)
    {
        var xbox = await AuthenticateAsync(microsoftToken, cancellationToken).ConfigureAwait(false);
        var xsts = await PostXstsAsync(xbox.Token, cancellationToken).ConfigureAwait(false);

        return await FinishAsync(xsts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Route for a title client id, which is what device-code sign-in uses. Xbox will not accept
    /// one at /user/authenticate at all: a title has to prove the device first and then go through
    /// SISU, which hands back the user, title and XSTS tokens in a single response.
    /// </summary>
    public async Task<MinecraftSession> ExchangeTitleAsync(
        string microsoftToken,
        string clientId,
        XboxDeviceIdentity device,
        CancellationToken cancellationToken = default)
    {
        var deviceToken = await AuthenticateDeviceAsync(device, cancellationToken).ConfigureAwait(false);

        var sisu = await SisuAuthorizeAsync(microsoftToken, clientId, deviceToken, device, cancellationToken)
            .ConfigureAwait(false);

        var authorization = sisu.AuthorizationToken
            ?? throw new MicrosoftAuthException("Xbox Live returned no authorization token.");

        return await FinishAsync(authorization, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MinecraftSession> FinishAsync(XboxResponse xsts, CancellationToken cancellationToken)
    {
        var userHash = xsts.DisplayClaims?.Xui?.FirstOrDefault()?.UserHash
            ?? throw new MicrosoftAuthException("Xbox Live returned no user hash.");

        var minecraft = await LoginToMinecraftAsync(userHash, xsts.Token, cancellationToken)
            .ConfigureAwait(false);

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUrl);
        profileRequest.Headers.Authorization = new("Bearer", minecraft.AccessToken);

        using var profileResponse = await http.SendAsync(profileRequest, cancellationToken).ConfigureAwait(false);
        if (profileResponse.StatusCode == HttpStatusCode.NotFound)
            throw new MicrosoftAuthException("This Microsoft account does not own Minecraft: Java Edition.");
        profileResponse.EnsureSuccessStatusCode();

        var profile = await profileResponse.Content
            .ReadFromJsonAsync<MinecraftProfile>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new MicrosoftAuthException("Minecraft returned an empty profile.");

        return new MinecraftSession(
            profile.Name,
            profile.Id,
            minecraft.AccessToken,
            "msa",
            xsts.DisplayClaims?.Xui?.FirstOrDefault()?.Xuid);
    }

    private async Task<XboxResponse> AuthenticateAsync(string microsoftToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, XboxAuthUrl)
        {
            Content = JsonContent.Create(new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = "d=" + microsoftToken,
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT",
            }),
        };

        request.Headers.Add("x-xbl-contract-version", "1");

        return await SendXboxAsync<XboxResponse>(request, "Xbox Live", cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> AuthenticateDeviceAsync(XboxDeviceIdentity device, CancellationToken cancellationToken)
    {
        var body = XboxDeviceIdentity.Serialize(new
        {
            Properties = new
            {
                DeviceType = XboxDeviceIdentity.DeviceType,
                Id = "{" + device.Id + "}",
                AuthMethod = "ProofOfPossession",
                ProofKey = device.ProofKey(),
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        });

        using var request = Signed(DeviceAuthUrl, "/device/authenticate", body, device);
        request.Headers.Add("x-xbl-contract-version", "1");

        var token = await SendXboxAsync<XboxResponse>(request, "Xbox device authentication", cancellationToken)
            .ConfigureAwait(false);

        return token.Token;
    }

    private async Task<SisuResponse> SisuAuthorizeAsync(
        string microsoftToken,
        string clientId,
        string deviceToken,
        XboxDeviceIdentity device,
        CancellationToken cancellationToken)
    {
        var body = XboxDeviceIdentity.Serialize(new
        {
            Sandbox = "RETAIL",
            UseModernGamertag = true,
            AppId = clientId,
            // The "t=" prefix belongs here, at SISU, and only for a title client id.
            AccessToken = "t=" + microsoftToken,
            DeviceToken = deviceToken,
            ProofKey = device.ProofKey(),
            RelyingParty = "rp://api.minecraftservices.com/",
        });

        using var request = Signed(SisuAuthorizeUrl, "/authorize", body, device);

        return await SendXboxAsync<SisuResponse>(request, "Xbox sign-in", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a request whose signature covers the exact bytes being sent. Serialising the body a
    /// second time would be enough to invalidate it.
    /// </summary>
    private static HttpRequestMessage Signed(string url, string path, byte[] body, XboxDeviceIdentity device)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("Signature", device.SignatureHeader("POST", path, authorization: null, body));

        return request;
    }

    private async Task<T> SendXboxAsync<T>(HttpRequestMessage request, string stage, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Xbox answers failures with a bare status and sometimes an XErr in the body. Naming
            // the hop and carrying the detail through beats a raw .NET status-code message, which
            // says nothing about which of four requests actually broke.
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new MicrosoftAuthException(
                $"{stage} failed ({(int)response.StatusCode})."
                + (detail is { Length: > 0 and < 300 } ? " " + detail.Trim() : ""));
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
            ?? throw new MicrosoftAuthException($"{stage} returned an empty response.");
    }

    /// <summary>
    /// Trades the XSTS token for a Minecraft session. Mojang only answers here for client ids they
    /// have allow-listed, so a 403 means the application isn't approved rather than anything being
    /// wrong with the sign-in itself.
    /// </summary>
    private async Task<MinecraftLoginResponse> LoginToMinecraftAsync(
        string userHash, string xstsToken, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(MinecraftLoginUrl, new
        {
            identityToken = $"XBL3.0 x={userHash};{xstsToken}",
        }, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new MicrosoftAuthException(
                "Minecraft refused this application. An Azure client id has to be approved by " +
                "Mojang before it can sign anyone in — apply at https://aka.ms/mce-reviewappid, " +
                "or switch this instance of Asobu to device-code sign-in in Settings.");

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<MinecraftLoginResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new MicrosoftAuthException("Minecraft returned an empty login response.");
    }

    private async Task<XboxResponse> PostXstsAsync(string xboxToken, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(XstsAuthUrl, new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xboxToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        }, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var failure = await response.Content.ReadFromJsonAsync<XstsError>(cancellationToken).ConfigureAwait(false);
            throw new MicrosoftAuthException(failure?.XErr switch
            {
                2148916233 => "This Microsoft account has no Xbox profile. Create one at xbox.com, then try again.",
                2148916235 => "Xbox Live is not available in this account's country.",
                2148916236 or 2148916237 => "This account needs adult verification before it can use Xbox Live.",
                2148916238 => "This is a child account. Add it to a Microsoft family group first.",
                _ => "Xbox Live rejected the sign-in.",
            });
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<XboxResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new MicrosoftAuthException("Xbox Live returned an empty response.");
    }

    private sealed class SisuResponse
    {
        public XboxResponse? AuthorizationToken { get; init; }
    }

    private sealed class XboxResponse
    {
        public string Token { get; init; } = "";
        public XboxDisplayClaims? DisplayClaims { get; init; }
    }

    private sealed class XboxDisplayClaims
    {
        public List<XboxUserInfo>? Xui { get; init; }
    }

    private sealed class XboxUserInfo
    {
        [JsonPropertyName("uhs")] public string? UserHash { get; init; }
        [JsonPropertyName("xid")] public string? Xuid { get; init; }
    }

    private sealed class XstsError
    {
        [JsonPropertyName("XErr")] public long XErr { get; init; }
    }

    private sealed class MinecraftLoginResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    }

    private sealed class MinecraftProfile
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
    }
}
