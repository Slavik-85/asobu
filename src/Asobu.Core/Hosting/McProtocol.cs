using System.Buffers.Binary;
using System.Text;

namespace Asobu.Core.Hosting;

/// <summary>
/// The handful of Minecraft wire primitives this folder needs: VarInts, length-prefixed strings,
/// and reading one whole packet without losing the bytes.
///
/// Deliberately not a protocol library. Asobu never interprets a game packet — it reads the
/// handshake to find out who is knocking and then moves bytes. Everything past the login packet
/// is opaque, which is why compression and encryption further down the stream cost nothing here.
/// </summary>
internal static class McProtocol
{
    /// <summary>
    /// A packet claiming to be larger than this is a mistake or an attack, and either way is not
    /// a handshake. Real ones are tens of bytes; the cap only has to be far enough above that.
    /// </summary>
    private const int MaxPacket = 64 * 1024;

    /// <summary>One packet, kept both raw and parsed: the raw form is what gets forwarded.</summary>
    internal sealed record Packet(byte[] Raw, byte[] Body);

    internal static void WriteVarInt(Stream to, int value)
    {
        var unsigned = (uint)value;
        while (true)
        {
            if (unsigned < 0x80)
            {
                to.WriteByte((byte)unsigned);
                return;
            }

            to.WriteByte((byte)(unsigned | 0x80));
            unsigned >>= 7;
        }
    }

    internal static void WriteString(Stream to, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(to, bytes.Length);
        to.Write(bytes);
    }

    /// <summary>Reads a VarInt from a buffer, moving <paramref name="at"/> past it.</summary>
    internal static int ReadVarInt(ReadOnlySpan<byte> from, ref int at)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (at >= from.Length) throw new IOException("Packet ended mid-number.");

            var b = from[at++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }

        throw new IOException("A VarInt ran longer than five bytes.");
    }

    internal static string ReadString(ReadOnlySpan<byte> from, ref int at)
    {
        var length = ReadVarInt(from, ref at);
        if (length < 0 || at + length > from.Length) throw new IOException("A string ran past the packet.");

        var text = Encoding.UTF8.GetString(from.Slice(at, length));
        at += length;
        return text;
    }

    internal static ushort ReadUShort(ReadOnlySpan<byte> from, ref int at)
    {
        if (at + 2 > from.Length) throw new IOException("Packet ended mid-number.");

        var value = BinaryPrimitives.ReadUInt16BigEndian(from.Slice(at, 2));
        at += 2;
        return value;
    }

    /// <summary>
    /// Reads one packet, keeping the length prefix alongside the body. The prefix is kept because
    /// the caller has to hand the whole thing on to the real server afterwards, byte for byte —
    /// re-encoding it would work right up until it didn't.
    /// </summary>
    internal static async Task<Packet> ReadPacketAsync(Stream from, CancellationToken cancellationToken)
    {
        var prefix = new List<byte>(5);
        var length = 0;

        for (var shift = 0; ; shift += 7)
        {
            if (shift >= 35) throw new IOException("A VarInt ran longer than five bytes.");

            var b = await ReadByteAsync(from, cancellationToken).ConfigureAwait(false);
            prefix.Add(b);
            length |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
        }

        if (length is <= 0 or > MaxPacket) throw new IOException($"A packet claimed to be {length} bytes.");

        var body = new byte[length];
        await from.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

        return new Packet([.. prefix, .. body], body);
    }

    private static async Task<byte> ReadByteAsync(Stream from, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        await from.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
        return one[0];
    }
}
