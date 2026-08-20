using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Asobu.Core.Accounts;

/// <summary>What to show the user while they finish signing in on another page or device.</summary>
public sealed record DeviceCodePrompt(string UserCode, string VerificationUri, DateTimeOffset ExpiresAt);

/// <summary>
/// Microsoft sign-in without an Azure app registration of our own, using the device code flow
/// against the legacy login.live.com endpoints and the Minecraft launcher's own public client id.
///
/// The trade-off, recorded here because it is not visible from the call site: the consent screen
/// the user sees says "Minecraft Launcher", not "Asobu", so they cannot tell what they are
/// authorising and cannot revoke Asobu without revoking the official launcher too. Microsoft can
/// also restrict this id at any time, which would break sign-in for everyone at once. The
/// registered-application route in <see cref="MicrosoftAuth"/> is the one to prefer once its
/// review lands; this exists so sign-in works before then.
/// </summary>
public sealed class DeviceCodeAuth(HttpClient http, TokenVault vault, XboxChain xbox)
{
    /// <summary>
    /// The Minecraft launcher's public client id. Not a secret — OAuth client ids never are, and
    /// this one is documented in every launcher library that does this.
    /// </summary>
    public const string MinecraftClientId = "00000000402b5328";

    private const string ConnectUrl = "https://login.live.com/oauth20_connect.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    /// <summary>The legacy endpoint's equivalent of XboxLive.signin.</summary>
    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";

    /// <summary>
    /// The legacy endpoint's own spelling. It also accepts the long urn: form, but this is what
    /// the official clients send and there is no reason to differ from them here.
    /// </summary>
    private const string DeviceCodeGrant = "device_code";

    /// <summary>
    /// Asks Microsoft for a code, hands it to the caller to display, then waits for the user to
    /// finish in their browser. The prompt callback fires once, before any polling starts.
    /// </summary>
    public async Task<(Account Account, MinecraftSession Session)> SignInAsync(
        Action<DeviceCodePrompt> onPrompt,
        CancellationToken cancellationToken = default)
    {
        var start = await RequestCodeAsync(cancellationToken).ConfigureAwait(false);

        onPrompt(new DeviceCodePrompt(
            start.UserCode!,
            string.IsNullOrWhiteSpace(start.VerificationUri) ? "https://www.microsoft.com/link" : start.VerificationUri,
            DateTimeOffset.UtcNow.AddSeconds(start.ExpiresIn <= 0 ? 900 : start.ExpiresIn)));

        var token = await PollAsync(start, cancellationToken).ConfigureAwait(false);

        var session = await ExchangeAsync(token.AccessToken!, cancellationToken).ConfigureAwait(false);

        var account = new Account
        {
            Uuid = session.Uuid,
            Username = session.Username,
            Kind = AccountKind.Microsoft,
            Method = AuthMethod.DeviceCode,
        };

        if (token.RefreshToken is { Length: > 0 } refresh) vault.Set(account.Uuid, refresh);

        return (account, session);
    }

    /// <summary>Refreshes silently. Throws when the stored token is gone or no longer accepted.</summary>
    public async Task<MinecraftSession> GetSessionAsync(Account account, CancellationToken cancellationToken = default)
    {
        if (vault.Get(account.Uuid) is not { Length: > 0 } refresh)
            throw new MicrosoftAuthException($"{account.Username} needs to sign in to Microsoft again.");

        var token = await PostFormAsync(TokenUrl, new Dictionary<string, string>
        {
            ["client_id"] = MinecraftClientId,
            ["refresh_token"] = refresh,
            ["grant_type"] = "refresh_token",
            ["scope"] = Scope,
        }, cancellationToken).ConfigureAwait(false);

        if (token.AccessToken is not { Length: > 0 })
        {
            // A refused refresh token is dead; drop it so the next launch prompts cleanly rather
            // than failing the same way forever.
            vault.Remove(account.Uuid);
            throw new MicrosoftAuthException($"{account.Username} needs to sign in to Microsoft again.");
        }

        // Microsoft rotates the refresh token on most refreshes; keeping the old one would work
        // until it silently didn't.
        if (token.RefreshToken is { Length: > 0 } rotated) vault.Set(account.Uuid, rotated);

        return await ExchangeAsync(token.AccessToken!, cancellationToken).ConfigureAwait(false);
    }

    public void SignOut(Account account) => vault.Remove(account.Uuid);

    /// <summary>
    /// Kept and reused rather than generated per sign-in: a fresh key pair every time would look
    /// to Xbox like a brand new machine on every launch.
    /// </summary>
    private Task<MinecraftSession> ExchangeAsync(string microsoftToken, CancellationToken cancellationToken) =>
        xbox.ExchangeTitleAsync(
            microsoftToken, MinecraftClientId, XboxDeviceIdentity.LoadOrCreate(vault), cancellationToken);

    private async Task<DeviceCodeResponse> RequestCodeAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(ConnectUrl, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = MinecraftClientId,
                ["scope"] = Scope,
                ["response_type"] = "device_code",
            }), cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var start = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken).ConfigureAwait(false);
        if (start?.DeviceCode is not { Length: > 0 } || start.UserCode is not { Length: > 0 })
            throw new MicrosoftAuthException("Microsoft didn't return a sign-in code.");

        return start;
    }

    private async Task<TokenResponse> PollAsync(DeviceCodeResponse start, CancellationToken cancellationToken)
    {
        // Microsoft's suggested interval, floored so a missing or silly value can't spin.
        var interval = TimeSpan.FromSeconds(Math.Max(start.Interval, 1));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(start.ExpiresIn <= 0 ? 900 : start.ExpiresIn);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow > deadline)
                throw new MicrosoftAuthException("The sign-in code expired. Start again to get a new one.");

            var token = await PostFormAsync(TokenUrl, new Dictionary<string, string>
            {
                ["client_id"] = MinecraftClientId,
                ["device_code"] = start.DeviceCode!,
                ["grant_type"] = DeviceCodeGrant,
            }, cancellationToken).ConfigureAwait(false);

            if (token.AccessToken is { Length: > 0 }) return token;

            switch (token.Error)
            {
                // The only non-terminal states: the user simply hasn't finished yet.
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;

                case "authorization_declined":
                    throw new MicrosoftAuthException("Sign-in was declined in the browser.");
                case "expired_token":
                    throw new MicrosoftAuthException("The sign-in code expired. Start again to get a new one.");
                default:
                    throw new MicrosoftAuthException(
                        token.ErrorDescription is { Length: > 0 } detail
                            ? $"Microsoft refused the sign-in: {detail}"
                            : "Microsoft refused the sign-in.");
            }
        }
    }

    /// <summary>
    /// The token endpoint answers 400 with a JSON body for the ordinary "not finished yet" case,
    /// so the status code is deliberately not checked here — the body is the answer.
    /// </summary>
    private async Task<TokenResponse> PostFormAsync(
        string url, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(url, new FormUrlEncodedContent(form), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false)
                ?? new TokenResponse();
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException)
        {
            throw new MicrosoftAuthException("Microsoft returned something unreadable during sign-in.");
        }
    }

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("user_code")] public string? UserCode { get; init; }
        [JsonPropertyName("device_code")] public string? DeviceCode { get; init; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("interval")] public int Interval { get; init; } = 5;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
    }
}
