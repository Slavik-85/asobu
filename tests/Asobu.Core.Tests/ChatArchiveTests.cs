using Asobu.Core;
using Asobu.Core.Online;

namespace Asobu.Core.Tests;

/// <summary>
/// Chat history kept on this computer, and the two ceilings that stop it growing forever.
///
/// The ceilings are the part worth pinning down. A history that quietly kept everything would
/// turn into gigabytes of somebody's pictures, and one that dropped the wrong end would throw
/// away this morning's conversation to save a week-old one.
/// </summary>
public class ChatArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-chat-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Me = "myuuid";
    private const string Them = "theiruuid";

    private ChatArchive Archive(long maxBytes = ChatArchive.DefaultMaxBytes, TimeSpan? keep = null) =>
        new(new AsobuPaths(_root), maxBytes, keep);

    private static byte[] Picture(int bytes) => new byte[bytes];

    [Fact]
    public void Keeps_what_was_said_in_order()
    {
        var archive = Archive();
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);

        archive.Append(Me, Them, start, mine: true, "first");
        archive.Append(Me, Them, start.AddMinutes(1), mine: false, "second");
        archive.Append(Me, Them, start.AddMinutes(2), mine: true, "third");

        var back = archive.Read(Me, Them);

        Assert.Equal(["first", "second", "third"], back.Select(l => l.Text));
        Assert.Equal([true, false, true], back.Select(l => l.Mine));
    }

    [Fact]
    public void Keeps_conversations_apart()
    {
        var archive = Archive();

        archive.Append(Me, Them, DateTimeOffset.UtcNow, mine: true, "to them");
        archive.Append(Me, "someoneelse", DateTimeOffset.UtcNow, mine: true, "to someone else");

        Assert.Equal(["to them"], archive.Read(Me, Them).Select(l => l.Text));
        Assert.Equal(["to someone else"], archive.Read(Me, "someoneelse").Select(l => l.Text));
    }

    /// <summary>Two accounts on one computer are two people, and must not read each other's chat.</summary>
    [Fact]
    public void Keeps_accounts_apart()
    {
        var archive = Archive();

        archive.Append("accountone", Them, DateTimeOffset.UtcNow, mine: true, "mine");

        Assert.Empty(archive.Read("accounttwo", Them));
    }

    [Fact]
    public void Keeps_pictures_as_files()
    {
        var archive = Archive();

        archive.AppendPicture(Me, Them, DateTimeOffset.UtcNow, mine: true, Picture(2048));

        var line = Assert.Single(archive.Read(Me, Them));
        Assert.Null(line.Text);
        Assert.NotNull(line.Picture);

        var path = archive.PicturePath(Me, line.Picture!);
        Assert.NotNull(path);
        Assert.Equal(2048, new FileInfo(path!).Length);
    }

    [Fact]
    public void Forgets_anything_older_than_the_week()
    {
        var archive = Archive(keep: TimeSpan.FromDays(7));
        var now = DateTimeOffset.UtcNow;

        archive.Append(Me, Them, now.AddDays(-9), mine: true, "ancient");
        archive.Append(Me, Them, now.AddDays(-8), mine: false, "also ancient");
        archive.Append(Me, Them, now.AddDays(-1), mine: true, "recent");

        archive.Prune(Me);

        Assert.Equal(["recent"], archive.Read(Me, Them).Select(l => l.Text));
    }

    /// <summary>A dropped picture must take its file with it, or the folder never actually shrinks.</summary>
    [Fact]
    public void Deletes_the_files_of_pictures_it_forgets()
    {
        var archive = Archive(keep: TimeSpan.FromDays(7));

        archive.AppendPicture(Me, Them, DateTimeOffset.UtcNow.AddDays(-9), mine: true, Picture(4096));
        var old = archive.Read(Me, Them).Single().Picture!;
        var path = archive.PicturePath(Me, old);
        Assert.NotNull(path);

        archive.Prune(Me);

        Assert.Empty(archive.Read(Me, Them));
        Assert.False(File.Exists(path!), "the picture's file outlived the line that referred to it");
    }

    [Fact]
    public void Comes_back_under_the_size_ceiling()
    {
        // Twenty pictures of 50 KB, against a ceiling of 200 KB.
        var archive = Archive(maxBytes: 200 * 1024);
        var start = DateTimeOffset.UtcNow.AddHours(-20);

        for (var i = 0; i < 20; i++)
            archive.AppendPicture(Me, Them, start.AddHours(i), mine: true, Picture(50 * 1024));

        archive.Prune(Me);

        var folder = Path.Combine(_root, "chat", Me);
        var weight = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);

        Assert.True(weight <= 200 * 1024, $"still {weight} bytes, over the ceiling");
        Assert.NotEmpty(archive.Read(Me, Them));
    }

    /// <summary>Dropping from the wrong end would throw away the conversation somebody is having.</summary>
    [Fact]
    public void Drops_the_oldest_first_when_it_is_over_size()
    {
        var archive = Archive(maxBytes: 200 * 1024);
        var start = DateTimeOffset.UtcNow.AddHours(-20);

        for (var i = 0; i < 20; i++)
            archive.AppendPicture(Me, Them, start.AddHours(i), mine: true, Picture(50 * 1024));

        archive.Prune(Me);

        var left = archive.Read(Me, Them);
        Assert.NotEmpty(left);

        // Whatever survived has to be the newest end of it, and still in order.
        Assert.Equal(left.OrderBy(l => l.At), left);
        Assert.True(left[^1].At > start.AddHours(15), "the newest messages were the ones thrown away");
    }

    /// <summary>The size ceiling must not let one busy conversation push out a quiet one.</summary>
    [Fact]
    public void Trims_every_conversation_rather_than_only_one()
    {
        var archive = Archive(maxBytes: 200 * 1024);
        var start = DateTimeOffset.UtcNow.AddHours(-30);

        for (var i = 0; i < 15; i++)
        {
            archive.AppendPicture(Me, "chatty", start.AddHours(i), mine: true, Picture(40 * 1024));
            archive.AppendPicture(Me, "quiet", start.AddHours(i), mine: false, Picture(1024));
        }

        archive.Prune(Me);

        // The quiet one contributed almost nothing to the size, so its recent messages should
        // survive rather than being cleared out alongside the noisy one's.
        Assert.NotEmpty(archive.Read(Me, "quiet"));
    }

    [Fact]
    public void Survives_a_half_written_line()
    {
        var archive = Archive();

        archive.Append(Me, Them, DateTimeOffset.UtcNow, mine: true, "good one");

        // What a launcher killed mid-append leaves behind.
        File.AppendAllText(Path.Combine(_root, "chat", Me, Them + ".log"), "{\"at\":\"2026-0");

        Assert.Equal(["good one"], archive.Read(Me, Them).Select(l => l.Text));
    }

    /// <summary>The uuids come from a server, so a name that walked out of the folder would be a hole.</summary>
    [Fact]
    public void Cannot_be_talked_out_of_its_own_folder()
    {
        var archive = Archive();

        archive.Append(Me, "../../escaped", DateTimeOffset.UtcNow, mine: true, "hello");

        Assert.False(File.Exists(Path.Combine(_root, "escaped.log")));
        Assert.False(File.Exists(Path.Combine(_root, "chat", "escaped.log")));

        var written = Directory.EnumerateFiles(_root, "*.log", SearchOption.AllDirectories).ToList();
        Assert.All(written, path =>
            Assert.StartsWith(Path.Combine(_root, "chat", Me), path, StringComparison.Ordinal));
    }

    [Fact]
    public void Reading_a_conversation_that_never_happened_is_empty()
    {
        Assert.Empty(Archive().Read(Me, "nobody"));
    }

    [Fact]
    public void Pruning_nothing_is_not_an_error()
    {
        Archive().Prune("never-said-anything");
    }
}
