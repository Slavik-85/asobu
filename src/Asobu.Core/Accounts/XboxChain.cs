using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

using System.Text.RegularExpressions;

namespace Asobu.Core.Accounts;

public sealed class MicrosoftAuthException(string message) : Exception(message);

/// <summary>
/// Microsoft token to Xbox Live to XSTS to a Minecraft session. Shared by both sign-in routes,
/// because everything after the first hop is identical no matter how the Microsoft token was
/// obtained — only the shape of the ticket differs.
/// </summary>
public sealed partial class XboxChain(HttpClient http)
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
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            throw new MicrosoftAuthException(Explain(stage, response, detail));
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
            ?? throw new MicrosoftAuthException($"{stage} returned an empty response.");
    }

    /// <summary>
    /// Turns a refusal into something the person reading it can act on.
    ///
    /// SISU answers a refusal with an empty body and puts its reason in an X-Err header — checked
    /// against the live endpoint, where a bad user token gives 401, no content at all, and
    /// X-Err: 4294967295. Reading only the body, as this used to, therefore produced a bare
    /// "failed (403)" carrying none of the one thing Xbox actually said.
    /// </summary>
    internal static string Explain(string stage, HttpResponseMessage response, string body)
    {
        var code = (int)response.StatusCode;

        if (XErr(response, body) is { Length: > 0 } xerr)
        {
            if (KnownXErrors.TryGetValue(xerr, out var meaning)) return meaning;

            return $"{stage} failed ({code}, Xbox code {xerr}).";
        }

        // A signed request whose clock is wrong is refused without any explanation at all, and a
        // laptop that has been asleep is exactly where that happens. Xbox's own Date header says
        // what it thinks the time is, so the two can be compared rather than guessed at.
        if (response.Headers.Date is { } theirs)
        {
            var drift = (DateTimeOffset.UtcNow - theirs).Duration();
            if (drift > TimeSpan.FromMinutes(5))
                return $"{stage} failed ({code}). This computer's clock is "
                     + $"{(int)drift.TotalMinutes} minutes off, and Xbox refuses sign-ins signed at "
                     + "the wrong time. Put the clock right and try again.";
        }

        // Nothing in the headers, nothing in the body. Xbox sits behind a bot filter that answers
        // this way when it turns a request away before Xbox ever sees it, which is a property of
        // the network rather than of the account — and worth saying, because the alternative is
        // somebody hunting for a fault in a Microsoft account that is perfectly fine.
        if (code == 403 && body.Length == 0)
            return $"{stage} failed (403) without saying why. Xbox turns requests away like this "
                 + "when it does not like where they came from: if you are on a VPN or a work "
                 + "network, try again without it.";

        return $"{stage} failed ({code})."
             + (body is { Length: > 0 and < 300 } ? " " + body.Trim() : "");
    }

    /// <summary>
    /// The XErr, from wherever this particular refusal put it: SISU sends an X-Err header and an
    /// empty body, XSTS sends a JSON body and no header, and some hops answer with a
    /// WWW-Authenticate challenge instead. All three, because which one arrives is not knowable
    /// from here.
    /// </summary>
    private static string? XErr(HttpResponseMessage response, string body)
    {
        if (response.Headers.TryGetValues("X-Err", out var direct)
            && direct.FirstOrDefault() is { Length: > 0 } header)
        {
            return header.Trim();
        }

        foreach (var challenge in response.Headers.WwwAuthenticate)
        {
            var found = XErrPattern().Match(challenge.Parameter ?? "");
            if (found.Success) return found.Groups[1].Value;
        }

        var inBody = XErrPattern().Match(body);
        return inBody.Success ? inBody.Groups[1].Value : null;
    }

    // XErr=2148916238 in a WWW-Authenticate challenge, "XErr":2148916238 in
    // an XSTS body. Same number, two spellings, and only one of them has an equals sign.
    [GeneratedRegex(@"""?XErr""?\s*[:=]\s*""?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex XErrPattern();

    /// <summary>
    /// What Xbox's own codes mean, written as the thing to do about them. Every one of these is a
    /// state of the person's Microsoft account rather than anything wrong with the launcher, and
    /// saying so saves them looking for a fault here.
    /// </summary>
    private static readonly Dictionary<string, string> KnownXErrors = new()
    {
        // Xbox's own "no comment". It means the token was refused, not that anything is wrong with
        // the account, and the cure is a fresh sign-in rather than anything on Microsoft's site.
        ["4294967295"] =
            "Xbox Live refused the sign-in. Sign out of this account in Asobu and add it again.",

        ["2148916227"] =
            "This Microsoft account has been banned from Xbox Live, so Minecraft cannot sign it in.",
        ["2148916233"] =
            "This Microsoft account has no Xbox profile yet. Sign in once at minecraft.net or "
            + "xbox.com to create one, then try again here.",
        // Turns up on a machine that has not signed in to Xbox before, because the acceptance is
        // per-account but the prompt only ever appears on Microsoft's own pages.
        ["2148916234"] =
            "This account has not accepted the Xbox Terms of Service. Sign in once at xbox.com, "
            + "accept them there, then try again here.",
        ["2148916235"] =
            "Xbox Live is not available in this account's country, so Minecraft cannot sign it in.",
        ["2148916236"] =
            "This account needs adult verification before it can sign in.",
        ["2148916237"] =
            "This account needs adult verification before it can sign in.",
        ["2148916238"] =
            "This is a child account. An adult has to add it to a Microsoft family before it can "
            + "sign in to Minecraft.",
    };

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

    /// <summary>
    /// XSTS puts its XErr in the response body rather than a header, but the codes are the same
    /// ones SISU uses, so this goes through the shared reader rather than keeping a second copy of
    /// the table that would drift out of step with the first.
    /// </summary>
    private async Task<XboxResponse> PostXstsAsync(string xboxToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, XstsAuthUrl)
        {
            Content = JsonContent.Create(new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xboxToken } },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT",
            }),
        };

        return await SendXboxAsync<XboxResponse>(request, "Xbox Live", cancellationToken).ConfigureAwait(false);
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
