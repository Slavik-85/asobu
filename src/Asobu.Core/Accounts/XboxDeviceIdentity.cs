using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asobu.Core.Accounts;

/// <summary>
/// This installation's Xbox device identity: an ECDSA P-256 key pair and a device id.
///
/// Xbox's SISU flow authenticates the *device* before the user, using proof of possession — every
/// request is signed with the private key, and the matching public key travels in the body as a
/// JWK. The key pair is generated once and kept, because rotating it makes Xbox treat each launch
/// as a brand new device.
/// </summary>
public sealed class XboxDeviceIdentity
{
    private const string KeyVaultEntry = "xbl.device.key";
    private const string IdVaultEntry = "xbl.device.id";

    /// <summary>Matches the platform the Java launcher's client id is registered for.</summary>
    public const string DeviceType = "Win32";

    private readonly ECDsa _key;

    public Guid Id { get; }

    private XboxDeviceIdentity(ECDsa key, Guid id)
    {
        _key = key;
        Id = id;
    }

    public static XboxDeviceIdentity LoadOrCreate(TokenVault vault)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var restored = false;

        if (vault.Get(KeyVaultEntry) is { Length: > 0 } stored)
        {
            try
            {
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored), out _);
                restored = true;
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                // An unreadable key means signing in again, not never signing in again: throw it
                // away and generate a fresh one.
                key.Dispose();
                key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            }
        }

        if (!restored) vault.Set(KeyVaultEntry, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));

        if (!Guid.TryParse(vault.Get(IdVaultEntry), out var id))
        {
            id = Guid.NewGuid();
            vault.Set(IdVaultEntry, id.ToString());
        }

        return new XboxDeviceIdentity(key, id);
    }

    /// <summary>The public half as the JWK Xbox expects in a ProofKey field.</summary>
    public object ProofKey()
    {
        var q = _key.ExportParameters(false).Q;

        return new
        {
            kty = "EC",
            alg = "ES256",
            crv = "P-256",
            use = "sig",
            x = Base64Url(q.X!),
            y = Base64Url(q.Y!),
        };
    }

    /// <summary>
    /// Xbox's request signature. The signed blob is a fixed sequence of fields separated by zero
    /// bytes, all big-endian — and it must cover the exact body bytes that get sent, which is why
    /// callers serialise once and hand the same array to both this and the request.
    /// </summary>
    public string SignatureHeader(string method, string pathAndQuery, string? authorization, byte[] body)
    {
        // Xbox wants a Windows FILETIME: 100-nanosecond ticks since 1601.
        var timestamp = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600L) * 10000000L;

        using var content = new MemoryStream();
        WriteInt32(content, 1);              // policy version
        content.WriteByte(0);
        WriteInt64(content, timestamp);
        content.WriteByte(0);
        Write(content, method);
        content.WriteByte(0);
        Write(content, pathAndQuery);
        content.WriteByte(0);
        Write(content, authorization ?? "");
        content.WriteByte(0);
        content.Write(body);
        content.WriteByte(0);

        // SignData returns IEEE P1363 (r||s) on .NET, which is the format Xbox expects.
        var signature = _key.SignData(content.ToArray(), HashAlgorithmName.SHA256);

        using var header = new MemoryStream();
        WriteInt32(header, 1);
        WriteInt64(header, timestamp);
        header.Write(signature);

        return Convert.ToBase64String(header.ToArray());
    }

    private static void Write(Stream stream, string value) =>
        stream.Write(Encoding.UTF8.GetBytes(value));

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Serialises a body once, so the bytes that are signed are the bytes that are sent.</summary>
    public static byte[] Serialize(object body) => JsonSerializer.SerializeToUtf8Bytes(body);
}
