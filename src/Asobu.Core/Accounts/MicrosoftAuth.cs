using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Asobu.Core.Accounts;

/// <summary>
/// Microsoft sign-in, done the way Mojang expects: system browser with PKCE, then the
/// Xbox Live to XSTS to Minecraft services exchange. Asobu never sees a password, and the
/// refresh token lives in the OS credential store (DPAPI on Windows), never in a file we own.
/// </summary>
public sealed class MicrosoftAuth(XboxChain xbox, AsobuPaths paths, string clientId)
{
    private static readonly string[] Scopes = ["XboxLive.signin", "offline_access"];

    private IPublicClientApplication? _app;

    public static bool IsConfigured(string? clientId) => clientId is { Length: > 0 };

    /// <summary>Interactive sign-in. Opens the user's own browser; no credentials pass through Asobu.</summary>
    public async Task<(Account Account, MinecraftSession Session)> SignInAsync(CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync().ConfigureAwait(false);

        var result = await app
            .AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        var session = await xbox.ExchangeAsync(result.AccessToken, cancellationToken)
            .ConfigureAwait(false);

        var account = new Account
        {
            Uuid = session.Uuid,
            Username = session.Username,
            Kind = AccountKind.Microsoft,
            Method = AuthMethod.Registered,
            HomeAccountId = result.Account?.HomeAccountId?.Identifier,
        };

        return (account, session);
    }

    /// <summary>
    /// Refreshes an existing account without prompting. Throws if the cached refresh token is
    /// gone, which is the caller's cue to run <see cref="SignInAsync"/> again.
    /// </summary>
    public async Task<MinecraftSession> GetSessionAsync(Account account, CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync().ConfigureAwait(false);

        var cached = (await app.GetAccountsAsync().ConfigureAwait(false))
            .FirstOrDefault(a => a.HomeAccountId?.Identifier == account.HomeAccountId);

        if (cached is null)
            throw new MicrosoftAuthException($"{account.Username} needs to sign in to Microsoft again.");

        try
        {
            var result = await app
                .AcquireTokenSilent(Scopes, cached)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            return await xbox.ExchangeAsync(result.AccessToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            throw new MicrosoftAuthException($"{account.Username} needs to sign in to Microsoft again.");
        }
    }

    public async Task SignOutAsync(Account account)
    {
        var app = await GetAppAsync().ConfigureAwait(false);
        foreach (var cached in await app.GetAccountsAsync().ConfigureAwait(false))
            if (cached.HomeAccountId?.Identifier == account.HomeAccountId)
                await app.RemoveAsync(cached).ConfigureAwait(false);
    }

    private async Task<IPublicClientApplication> GetAppAsync()
    {
        if (_app is not null) return _app;

        if (!IsConfigured(clientId))
            throw new MicrosoftAuthException(
                "No Microsoft client id is configured. Add an Azure app registration id to settings.json " +
                "as \"microsoftClientId\" — Minecraft sign-in requires one that Mojang has approved.");

        var app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AadAuthorityAudience.PersonalMicrosoftAccount)
            .WithRedirectUri("http://localhost")
            .Build();

        // MSAL's own encrypted cache: DPAPI on Windows, Keychain on macOS, Secret Service on Linux.
        var storage = new StorageCreationPropertiesBuilder("msal.cache", paths.Root)
            .WithMacKeyChain("Asobu", "MicrosoftAccount")
            .WithLinuxKeyring("cc.asobu.tokens", "default", "Asobu Microsoft tokens",
                new KeyValuePair<string, string>("app", "asobu"),
                default)
            .Build();

        var cache = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
        cache.RegisterCache(app.UserTokenCache);

        return _app = app;
    }
}
