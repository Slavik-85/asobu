using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asobu.Core.Accounts;

/// <summary>
/// Refresh tokens, protected at rest.
///
/// MSAL brings its own encrypted cache, but the device-code flow talks to login.live.com directly
/// and so has to keep its own. On Windows the file is sealed with DPAPI under the current user
/// account, which means another user on the same machine cannot read it and copying the file to
/// another machine yields nothing.
///
/// Linux has no DPAPI. The file is plain JSON there, kept private by being created readable only
/// by its owner — the same trade Prism makes. A keyring would be better against someone already
/// running code as you; against the case that actually happens, another account on a shared
/// machine reading the file, 0600 is the whole of the defence and it is enough.
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

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(VaultFile, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            return;
        }

        // The mode is set as the file is created rather than afterwards. chmod after the write
        // would leave a window — however brief — in which a token sat on disk readable by every
        // account on the machine, and a window is all a shared machine needs.
        using (var file = new FileStream(VaultFile, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            file.Write(plain);
        }

        // UnixCreateMode only applies to a file being created, so a vault written by an older
        // build keeps whatever mode it was born with until this puts it right.
        try
        {
            File.SetUnixFileMode(VaultFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A filesystem with no modes to set — a FAT-formatted USB stick in portable mode,
            // most likely. Refusing to save the token would be the worse answer.
        }
    }
}
