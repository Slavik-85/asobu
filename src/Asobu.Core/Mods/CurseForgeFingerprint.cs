namespace Asobu.Core.Mods;

/// <summary>
/// CurseForge's file fingerprint: MurmurHash2, 32-bit, seed 1, over the file with its whitespace
/// taken out first.
///
/// The whitespace part is not an optimisation — it is the algorithm. CurseForge strips tab, line
/// feed, carriage return and space before hashing, so that a jar rebuilt with different line
/// endings still fingerprints the same. Both the stripped bytes and the stripped length go into
/// the hash, which is why the length is taken after filtering rather than before.
///
/// This exists because CurseForge has no hash lookup of the ordinary kind. Modrinth will name a
/// project from a SHA-1; CurseForge will only do it from this.
/// </summary>
public static class CurseForgeFingerprint
{
    private const uint Multiplier = 0x5bd1e995;
    private const int Rotation = 24;
    private const uint Seed = 1;

    public static async Task<uint> OfFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        return Of(bytes);
    }

    public static uint Of(ReadOnlySpan<byte> file)
    {
        var data = Strip(file);
        var length = (uint)data.Length;

        var hash = Seed ^ length;
        var index = 0;

        while (length >= 4)
        {
            var block = (uint)(data[index] | (data[index + 1] << 8) | (data[index + 2] << 16) | (data[index + 3] << 24));

            block *= Multiplier;
            block ^= block >> Rotation;
            block *= Multiplier;

            hash *= Multiplier;
            hash ^= block;

            index += 4;
            length -= 4;
        }

        // The tail, largest piece first — the cases fall through on purpose.
        switch (length)
        {
            case 3:
                hash ^= (uint)(data[index + 2] << 16);
                goto case 2;
            case 2:
                hash ^= (uint)(data[index + 1] << 8);
                goto case 1;
            case 1:
                hash ^= data[index];
                hash *= Multiplier;
                break;
        }

        hash ^= hash >> 13;
        hash *= Multiplier;
        hash ^= hash >> 15;

        return hash;
    }

    /// <summary>Tab, line feed, carriage return and space, removed before anything is hashed.</summary>
    private static byte[] Strip(ReadOnlySpan<byte> file)
    {
        var kept = new byte[file.Length];
        var count = 0;

        foreach (var b in file)
            if (b is not (9 or 10 or 13 or 32))
                kept[count++] = b;

        return kept[..count];
    }
}
