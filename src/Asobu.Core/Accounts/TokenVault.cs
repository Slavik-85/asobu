using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asobu.Core.Accounts;

/// <summary>
/// Refresh tokens, encrypted at rest.
///
/// MSAL brings its own encrypted cache, but the device-code flow talks to login.live.com directly
/// and so has to keep its own. On Windows the file is sealed with DPAPI under the current user
/// account, which means another user on the same machine cannot read it and copying the file to
/// another machine yields nothing.
/// </summary>
public sealed class TokenVault(AsobuPaths paths)
{
    /// <summary>
    /// Mixed into the DPAPI key. Not a secret — it only scopes the ciphertext to this vault, so a
    /// blob lifted from here can't be fed to some other DPAPI reader on the same account.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("asobu.tokenvault.v1");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private string VaultFile => Path.Combine(paths.Root, "tokens.dat");

    public string? Get(string key) => Read().GetValueOrDefault(key);

    public void Set(string key, string token)
    {
        var all = Read();
        all[key] = token;
        Write(all);
    }

    public void Remove(string key)
    {
        var all = Read();
        if (all.Remove(key)) Write(all);
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(VaultFile)) return [];

            var sealedBytes = File.ReadAllBytes(VaultFile);
            var plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser)
                : sealedBytes;

            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain, Options) ?? [];
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            // A vault written by another user account, or a truncated file, is unreadable rather
            // than fatal: the accounts simply need signing in again.
            return [];
        }
    }

    private void Write(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(paths.Root);

        var plain = JsonSerializer.SerializeToUtf8Bytes(all, Options);

        // DPAPI is Windows-only. Elsewhere this lands as plain JSON in the launcher's own folder,
        // which is worth knowing about before Asobu ships anywhere but Windows.
        var payload = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
            : plain;

        File.WriteAllBytes(VaultFile, payload);
    }
}
