using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asobu.Core.Accounts;

[JsonConverter(typeof(JsonStringEnumConverter<AccountKind>))]
public enum AccountKind
{
    Offline,
    Microsoft,
}

/// <summary>How a Microsoft account signs in, and therefore how it refreshes later.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuthMethod>))]
public enum AuthMethod
{
    /// <summary>Device code against login.live.com with the bundled Minecraft client id.</summary>
    DeviceCode,

    /// <summary>Our own Azure app registration, browser redirect, MSAL's token cache.</summary>
    Registered,
}

public sealed class Account
{
    public required string Uuid { get; set; }
    public required string Username { get; set; }
    public AccountKind Kind { get; set; }

    /// <summary>
    /// Recorded per account rather than read from settings at refresh time: someone who signed in
    /// one way keeps working after the launcher's default is switched to the other.
    /// </summary>
    public AuthMethod Method { get; set; }

    /// <summary>MSAL's account identifier, used to refresh silently. Never a token.</summary>
    public string? HomeAccountId { get; set; }

    /// <summary>
    /// What the friends network calls this offline account, once somebody has asked to put it
    /// there. Null until then, and null forever for a Microsoft account, whose identity on the
    /// network is the Minecraft one Mojang already vouches for.
    ///
    /// Kept apart from <see cref="Uuid"/> on purpose. That one is derived from the name alone, so
    /// every Steve in the world has the same — as a network identity it would make them one
    /// account reading each other's messages.
    /// </summary>
    public string? NetworkUuid { get; set; }

    /// <summary>The four digits friends type after the name to find this account.</summary>
    public string? NetworkTag { get; set; }

    /// <summary>The name as the network shows it: plain for Microsoft, name#tag for offline.</summary>
    [JsonIgnore]
    public string NetworkHandle => NetworkTag is { Length: > 0 } tag ? $"{Username}#{tag}" : Username;

    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsOnline => Kind == AccountKind.Microsoft;

    /// <summary>
    /// Minecraft's offline UUID: a version-3 UUID over "OfflinePlayer:&lt;name&gt;". Deriving it the
    /// same way the game does keeps a player's world data attached to them across launches.
    /// </summary>
    public static string OfflineUuid(string username)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexStringLower(hash);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    public static Account CreateOffline(string username) => new()
    {
        Uuid = OfflineUuid(username),
        Username = username,
        Kind = AccountKind.Offline,
    };
}

/// <summary>What the game actually needs to start. Never persisted — tokens stay in the OS keystore.</summary>
public sealed record MinecraftSession(string Username, string Uuid, string AccessToken, string UserType, string? Xuid)
{
    public static MinecraftSession ForOffline(Account account) =>
        new(account.Username, account.Uuid, "0", "legacy", null);
}

/// <summary>The account list. Identities only; no secrets are written here.</summary>
public sealed class AccountStore(AsobuPaths paths)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public List<Account> Load()
    {
        try
        {
            if (File.Exists(paths.AccountsFile))
                return JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(paths.AccountsFile), Options) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
        }

        return [];
    }

    public void Save(IEnumerable<Account> accounts)
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.AccountsFile, JsonSerializer.Serialize(accounts, Options));
    }
}
