using System.Text;
using System.Text.Json;

namespace Asobu.Core.Online;

/// <summary>One thing said, as it is kept on disk.</summary>
/// <param name="At">When it was said.</param>
/// <param name="Mine">True if we sent it, false if they did.</param>
/// <param name="Text">What was said, for a line that is words.</param>
/// <param name="Picture">The file name under pictures/, for a line that is one.</param>
public sealed record ArchivedLine(DateTimeOffset At, bool Mine, string? Text, string? Picture);

/// <summary>
/// Chat history, kept on this computer only.
///
/// The server relays messages and keeps none of them — that is the whole design of it, and this
/// does not change it. What changes is that the launcher no longer forgets a conversation the
/// moment it closes. Nothing here is ever sent anywhere; it is written under the launcher's own
/// folder and read back when the conversation is opened again.
///
/// It is worth being plain about one thing: this is written in the clear. Messages are encrypted
/// in flight so that the server cannot read them, and that remains true. Once decrypted on your
/// own machine they are ordinary text and ordinary pictures, and anything running as you can read
/// them — which is equally true of a browser's history or any other chat program's cache.
///
/// Two ceilings keep it from growing forever, and both are enforced together: nothing older than
/// a week is kept, and the whole archive stays under half a gigabyte. Age does nearly all of the
/// work; the size cap is there for the case age does not cover, which in practice means pictures.
/// </summary>
/// <param name="maxBytes">The size ceiling. Lowered by tests, which cannot write half a gigabyte.</param>
/// <param name="keep">How long to keep anything. Likewise.</param>
public sealed class ChatArchive(AsobuPaths paths, long maxBytes = ChatArchive.DefaultMaxBytes, TimeSpan? keep = null)
{
    /// <summary>How long anything is kept, whatever the size.</summary>
    public static readonly TimeSpan DefaultKeep = TimeSpan.FromDays(7);

    /// <summary>And how much, whatever the age. Pictures are what make this reachable at all.</summary>
    public const long DefaultMaxBytes = 500L * 1024 * 1024;

    private readonly TimeSpan _keep = keep ?? DefaultKeep;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// One folder per account, so two people sharing a computer do not share a history — and so
    /// signing out of one leaves the other's alone.
    /// </summary>
    private string FolderFor(string me) => Path.Combine(paths.Root, "chat", Safe(me));

    private string LogFor(string me, string friend) =>
        Path.Combine(FolderFor(me), Safe(friend) + ".log");

    private string PicturesFor(string me) => Path.Combine(FolderFor(me), "pictures");

    /// <summary>
    /// A uuid is already safe to use as a file name, but this is never trusted to be one: it
    /// arrives from a server, and a name that walked up out of the folder would be a hole.
    /// </summary>
    private static string Safe(string id)
    {
        var clean = new string([.. id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
        return clean.Length > 0 ? clean[..Math.Min(clean.Length, 64)] : "unknown";
    }

    /// <summary>Everything still kept from one conversation, oldest first.</summary>
    public IReadOnlyList<ArchivedLine> Read(string me, string friend)
    {
        var log = LogFor(me, friend);
        if (!File.Exists(log)) return [];

        var lines = new List<ArchivedLine>();
        try
        {
            foreach (var raw in File.ReadLines(log))
            {
                if (raw.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<ArchivedLine>(raw, Json) is { } line) lines.Add(line);
                }
                catch (JsonException)
                {
                    // One unreadable line loses one message rather than the conversation. A
                    // half-written last line is the ordinary way this happens — the launcher
                    // was killed mid-append — and the rest of the file is perfectly good.
                }
            }
        }
        catch (IOException)
        {
            return lines;
        }

        return lines;
    }

    /// <summary>Adds a line of words to a conversation.</summary>
    public void Append(string me, string friend, DateTimeOffset at, bool mine, string text) =>
        Write(me, friend, new ArchivedLine(at, mine, text, null));

    /// <summary>
    /// Adds a picture, kept as a file of its own beside the log.
    ///
    /// Separately rather than inline as base64: it keeps the log small enough to read line by
    /// line, and it makes the size of the archive something that can be measured by asking the
    /// file system rather than by parsing everything in it.
    /// </summary>
    public void AppendPicture(string me, string friend, DateTimeOffset at, bool mine, byte[] jpeg)
    {
        try
        {
            Directory.CreateDirectory(PicturesFor(me));

            var name = Guid.NewGuid().ToString("n") + ".jpg";
            File.WriteAllBytes(Path.Combine(PicturesFor(me), name), jpeg);

            Write(me, friend, new ArchivedLine(at, mine, null, name));
        }
        catch (IOException)
        {
            // A picture that could not be saved is still a picture that was sent and shown. The
            // conversation on screen is the thing that matters; the archive is a convenience.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The full path of an archived picture, or null when the file is gone.</summary>
    public string? PicturePath(string me, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var path = Path.Combine(PicturesFor(me), Safe(Path.GetFileNameWithoutExtension(name)) + ".jpg");
        return File.Exists(path) ? path : null;
    }

    private void Write(string me, string friend, ArchivedLine line)
    {
        try
        {
            Directory.CreateDirectory(FolderFor(me));
            File.AppendAllText(LogFor(me, friend), JsonSerializer.Serialize(line, Json) + "\n", Encoding.UTF8);
        }
        catch (IOException)
        {
            // Same reasoning as a picture that would not save: losing the record of a message is
            // not worth interrupting the conversation it belongs to.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Enforces both ceilings, oldest first.
    ///
    /// Age is applied to every conversation, then size to what is left — in that order, because
    /// dropping a week-old picture is free and dropping this morning's is not. Called on the way
    /// in rather than on a timer: the only moment the size can have grown is one where something
    /// was written, and the only moment anyone cares is one where it is about to be read.
    /// </summary>
    public void Prune(string me)
    {
        var folder = FolderFor(me);
        if (!Directory.Exists(folder)) return;

        try
        {
            var cutoff = DateTimeOffset.UtcNow - _keep;
            var kept = new List<(string Log, ArchivedLine Line)>();

            foreach (var log in Directory.GetFiles(folder, "*.log"))
            {
                var surviving = new List<ArchivedLine>();

                foreach (var line in ReadLog(log))
                {
                    if (line.At < cutoff) Forget(me, line);
                    else surviving.Add(line);
                }

                Rewrite(log, surviving);
                foreach (var line in surviving) kept.Add((log, line));
            }

            var total = Weight(me, folder);
            if (total <= maxBytes) return;

            // Still too big. Drop from the oldest end, across every conversation at once, until
            // it fits — a size cap that only trimmed one conversation would let a busy one push
            // out a quiet one that had done nothing wrong.
            kept.Sort((a, b) => a.Line.At.CompareTo(b.Line.At));

            // Counted down as each one goes, rather than re-measuring the folder between drops.
            // Measuring is what costs here, and doing it per drop was the reason an earlier
            // version dropped in batches — which overshot, and emptied an archive that needed
            // one picture removing.
            var index = 0;
            while (index < kept.Count && total > maxBytes)
            {
                total -= SizeOf(me, kept[index].Line);
                Forget(me, kept[index].Line);
                index++;
            }

            // Every log that had anything in it is rewritten, including the ones left with
            // nothing — that is what deletes them rather than leaving an empty file behind.
            var survivors = kept.Skip(index).ToList();
            foreach (var log in kept.Select(k => k.Log).Distinct())
                Rewrite(log, [.. survivors.Where(s => s.Log == log).Select(s => s.Line)]);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private IEnumerable<ArchivedLine> ReadLog(string log)
    {
        List<ArchivedLine> lines = [];
        try
        {
            foreach (var raw in File.ReadLines(log))
            {
                if (raw.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<ArchivedLine>(raw, Json) is { } line) lines.Add(line);
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        return lines;
    }

    private static void Rewrite(string log, IReadOnlyList<ArchivedLine> lines)
    {
        try
        {
            if (lines.Count == 0)
            {
                File.Delete(log);
                return;
            }

            var text = new StringBuilder();
            foreach (var line in lines) text.Append(JsonSerializer.Serialize(line, Json)).Append('\n');

            // Through a temporary file, so a launcher closed mid-rewrite loses nothing: either
            // the old conversation is there or the new one is, never half of each.
            var temp = log + ".tmp";
            File.WriteAllText(temp, text.ToString(), Encoding.UTF8);
            File.Move(temp, log, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Roughly what one line costs on disk: its picture, if it has one, plus the bytes its own
    /// entry in the log takes. Approximate on purpose — this is used to count down towards a
    /// ceiling, and being a few bytes out on the JSON punctuation changes nothing.
    /// </summary>
    private long SizeOf(string me, ArchivedLine line)
    {
        const long lineOverhead = 120;

        if (line.Picture is { Length: > 0 } name && PicturePath(me, name) is { } path)
        {
            try
            {
                return new FileInfo(path).Length + lineOverhead;
            }
            catch (IOException)
            {
                return lineOverhead;
            }
        }

        return (line.Text?.Length ?? 0) + lineOverhead;
    }

    /// <summary>Deletes the picture a line refers to, if it is one.</summary>
    private void Forget(string me, ArchivedLine line)
    {
        if (line.Picture is not { Length: > 0 } name) return;

        try
        {
            if (PicturePath(me, name) is { } path) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>What the archive weighs, logs and pictures together.</summary>
    private static long Weight(string me, string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
