using System.Security.Cryptography;
using System.Text;
using Asobu.Core.Accounts;

namespace Asobu.Core.Online;

/// <summary>
/// End-to-end encryption for chat.
///
/// Each account holds an ECDH key pair. The public half goes to the server so friends can fetch
/// it; the private half never leaves the machine, living in the same vault as the Microsoft
/// refresh token. Two friends derive the same secret from their own private key and the other's
/// public one, and messages are sealed with AES-GCM under it. The server relays a blob it has no
/// key for.
///
/// What this does <i>not</i> protect against, stated plainly because a security claim with an
/// unstated hole in it is worse than no claim:
///
/// <list type="bullet">
/// <item>
/// The server hands out the public keys. A server that lied — handing you its own key instead of
/// your friend's — could sit in the middle and read everything. Nothing here can rule that out on
/// its own, which is why every conversation shows a fingerprint the two of you can compare
/// somewhere else. Matching fingerprints mean nobody is in the middle.
/// </item>
/// <item>
/// Who talks to whom, when, and how much is not hidden. The server has to know where to send a
/// message, so it knows that much whatever the contents say.
/// </item>
/// <item>
/// The secret is derived from long-lived keys rather than fresh ones per message, so this has no
/// forward secrecy: somebody who later stole a private key and had kept a recording of the
/// ciphertext could read it. The recording is the hard part — the server keeps nothing, so there
/// is no archive to steal.
/// </item>
/// </list>
/// </summary>
public sealed class MessageCrypto(TokenVault vault)
{
    /// <summary>
    /// P-256, matching the curve the Xbox device identity already uses. Chosen for being in the
    /// framework rather than for being the best available: a curve .NET implements is one nobody
    /// here has to implement, and hand-rolled cryptography is how this sort of thing goes wrong.
    /// </summary>
    private static readonly ECCurve Curve = ECCurve.NamedCurves.nistP256;

    /// <summary>Separates this use of the shared secret from any other. Not a secret itself.</summary>
    private static readonly byte[] Purpose = "asobu.chat.v1"u8.ToArray();

    private const int NonceBytes = 12;   // AES-GCM's standard, and the only size worth using
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    private static string VaultKey(string accountUuid) => "chatkey:" + accountUuid;

    /// <summary>
    /// This account's key pair, made on first use and kept thereafter.
    ///
    /// Kept rather than rotated because the public half is published: a new key pair means every
    /// friend holding the old one can no longer read anything until they see the new one, which
    /// is a broken conversation rather than a security improvement.
    /// </summary>
    public ECDiffieHellman MineFor(string accountUuid)
    {
        var key = ECDiffieHellman.Create(Curve);

        if (vault.Get(VaultKey(accountUuid)) is { Length: > 0 } stored)
        {
            try
            {
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored), out _);
                return key;
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                // Unreadable, so it may as well not exist. A fresh pair costs the ability to read
                // anything already in flight, which is a message or two, and restores the ability
                // to read everything after.
                key.Dispose();
                key = ECDiffieHellman.Create(Curve);
            }
        }

        vault.Set(VaultKey(accountUuid), Convert.ToBase64String(key.ExportPkcs8PrivateKey()));

        return key;
    }

    /// <summary>The public half, as the server and friends see it.</summary>
    public static string PublicKeyOf(ECDiffieHellman mine) =>
        Convert.ToBase64String(mine.PublicKey.ExportSubjectPublicKeyInfo());

    /// <summary>
    /// Seals a message for one friend. What comes back is base64 of nonce, ciphertext and tag,
    /// which is all the server ever sees.
    /// </summary>
    public static string Seal(ECDiffieHellman mine, string theirPublicKey, string text)
    {
        var key = SharedKey(mine, theirPublicKey);

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plain = Encoding.UTF8.GetBytes(text);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using (var gcm = new AesGcm(key, TagBytes))
        {
            gcm.Encrypt(nonce, plain, cipher, tag);
        }

        CryptographicOperations.ZeroMemory(key);

        var box = new byte[NonceBytes + cipher.Length + TagBytes];
        nonce.CopyTo(box, 0);
        cipher.CopyTo(box, NonceBytes);
        tag.CopyTo(box, NonceBytes + cipher.Length);

        return Convert.ToBase64String(box);
    }

    /// <summary>
    /// Opens a message from one friend, or null when it will not open.
    ///
    /// Null rather than an exception because every reason it happens is ordinary: a friend who
    /// changed keys, a message from before either side had any, or something corrupted on the
    /// way. None of them is worth crashing a chat window over — and AES-GCM refusing to open a
    /// tampered message is exactly the point, so a failure here is the system working.
    /// </summary>
    public static string? Open(ECDiffieHellman mine, string theirPublicKey, string box)
    {
        byte[] key;
        byte[] raw;

        try
        {
            raw = Convert.FromBase64String(box);
            key = SharedKey(mine, theirPublicKey);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }

        if (raw.Length < NonceBytes + TagBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            return null;
        }

        var nonce = raw.AsSpan(0, NonceBytes);
        var cipher = raw.AsSpan(NonceBytes, raw.Length - NonceBytes - TagBytes);
        var tag = raw.AsSpan(raw.Length - TagBytes);
        var plain = new byte[cipher.Length];

        try
        {
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Decrypt(nonce, cipher, tag, plain);

            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// A short code both ends of a conversation can read out to each other.
    ///
    /// This is the only thing that can catch a server handing out the wrong key. Derived from
    /// both public keys sorted, so the two of you compute the same code without agreeing who
    /// goes first, and shown as digits because they survive being read aloud where base64 does
    /// not.
    /// </summary>
    public static string Fingerprint(string onePublicKey, string otherPublicKey)
    {
        var (first, second) = string.CompareOrdinal(onePublicKey, otherPublicKey) <= 0
            ? (onePublicKey, otherPublicKey)
            : (otherPublicKey, onePublicKey);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(first + "\n" + second));

        var groups = new string[5];
        for (var i = 0; i < groups.Length; i++)
        {
            // Five digits per group out of four bytes, which is under the 65536 a pair gives and
            // so keeps every group the same width.
            var slice = BitConverter.ToUInt32(digest, i * 4) % 100000;
            groups[i] = slice.ToString("D5");
        }

        return string.Join(" ", groups);
    }

    /// <summary>
    /// The key two friends share, from one private half and one public one. Both sides reach the
    /// same bytes without either sending them.
    /// </summary>
    private static byte[] SharedKey(ECDiffieHellman mine, string theirPublicKey)
    {
        using var theirs = ECDiffieHellman.Create(Curve);
        theirs.ImportSubjectPublicKeyInfo(Convert.FromBase64String(theirPublicKey), out _);

        // DeriveKeyFromHash rather than the raw agreement plus HKDF: the raw form is not
        // implemented on every platform .NET runs on and throws where it is not, which would be a
        // Linux-only failure discovered by a Linux user. This one is everywhere, and hashing the
        // agreement with a purpose appended is a KDF either way.
        return mine.DeriveKeyFromHash(theirs.PublicKey, HashAlgorithmName.SHA256,
            secretPrepend: null, secretAppend: Purpose)[..KeyBytes];
    }
}
