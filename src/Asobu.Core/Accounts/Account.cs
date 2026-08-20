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
