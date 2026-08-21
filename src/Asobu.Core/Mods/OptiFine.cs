using System.Text.RegularExpressions;

namespace Asobu.Core.Mods;

/// <summary>One OptiFine build, as its own download page lists it.</summary>
/// <param name="FileName">"OptiFine_1.8.9_HD_U_M5.jar", which carries everything below in it.</param>
/// <param name="MinecraftVersion">The version it is built for.</param>
/// <param name="Edition">HD_U and the like.</param>
/// <param name="Release">The build letter and number — M5, J9 — newest last alphabetically.</param>
/// <param name="Preview">A build the author has not called final. Only used when nothing else fits.</param>
public sealed record OptiFineBuild(
    string FileName,
    string MinecraftVersion,
    string Edition,
    string Release,
    bool Preview);

/// <summary>
/// OptiFine, which has no API and no Maven, only a downloads page.
///
/// It is the one thing Asobu installs by reading somebody's website, and that is not a choice —
/// OptiFine is not on Modrinth or CurseForge and never has been, its author distributes it from
/// optifine.net alone, and the alternative is telling people to go and fetch a jar by hand for
/// the Forge versions where nothing else does the job.
///
/// Two requests, because the download link is not a fixed address. The list page names the files;
/// asking for one of them by name returns a page carrying a link with a token on it, and the
/// token is what the real download wants. Scraped fresh every time rather than remembered: it is
/// the site's to change, and a stale one fails as a download rather than as an error worth
/// reading.
///
/// Written to fail quietly. A page that has been redesigned means no builds are found, which
/// shows as OptiFine being unavailable — the same as it being unavailable for any other reason,
/// and far better than a launch that dies because a regular expression stopped matching.
/// </summary>
public sealed partial class OptiFine(HttpClient http)
{
    /// <summary>
    /// What an instance records when OptiFine is its performance mod. Not a Modrinth slug like
    /// the other two, because OptiFine is not on Modrinth — the install path checks for this name
    /// and goes to the website instead.
    /// </summary>
    public const string Marker = "optifine";

    private const string Site = "https://optifine.net/";
    private const string Downloads = Site + "downloads";

    /// <summary>
    /// The site answers a bare client with an interstitial. This is the same claim any browser
    /// makes and nothing more — no key, no account, nothing identifying.
    /// </summary>
    private const string Browser =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/125.0 Safari/537.36";

    /// <summary>The whole list, read once per launcher run. It changes a few times a year.</summary>
    private IReadOnlyList<OptiFineBuild>? _all;
    private readonly SemaphoreSlim _reading = new(1, 1);

    /// <summary>Every build the site lists, newest Minecraft first as the page orders them.</summary>
    public async Task<IReadOnlyList<OptiFineBuild>> GetBuildsAsync(CancellationToken cancellationToken = default)
    {
        if (_all is { } remembered) return remembered;

        await _reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_all is { } second) return second;

            var html = await GetAsync(Downloads, cancellationToken).ConfigureAwait(false);
            if (html is null) return _all = [];

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var builds = new List<OptiFineBuild>();

            foreach (Match match in FilePattern().Matches(html))
            {
                var fileName = match.Groups["file"].Value;
                if (!seen.Add(fileName)) continue;      // the page links each file twice

                if (Describe(fileName) is { } build) builds.Add(build);
            }

            return _all = builds;
        }
        catch (Exception)
        {
            return _all = [];
        }
        finally
        {
            _reading.Release();
        }
    }

    /// <summary>
    /// The newest finished build for one Minecraft version, or null when there is none.
    ///
    /// Previews are never offered. They are the author's unfinished work, published for people
    /// who have chosen to try it — installing one on somebody's behalf because it was the newest
    /// thing on the page is a decision they did not make. A version with only previews is a
    /// version OptiFine does not have yet, as far as this is concerned.
    ///
    /// Ordered by the release letter and number the file carries — M5 is after M4 and after L9 —
    /// rather than by where the page happened to list it.
    /// </summary>
    public async Task<OptiFineBuild?> GetLatestAsync(
        string minecraftVersion, CancellationToken cancellationToken = default)
    {
        var builds = await GetBuildsAsync(cancellationToken).ConfigureAwait(false);

        return builds
            .Where(b => !b.Preview)
            .Where(b => b.MinecraftVersion.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(b => b.Release, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// Which Minecraft versions OptiFine has a finished build for.
    ///
    /// The same rule as above, and for the same reason: a version this says yes to is one the
    /// checkbox will offer, so it must not say yes on the strength of a preview it would then
    /// refuse to install.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetGameVersionsAsync(CancellationToken cancellationToken = default)
    {
        var builds = await GetBuildsAsync(cancellationToken).ConfigureAwait(false);

        return [.. builds
            .Where(b => !b.Preview)
            .Select(b => b.MinecraftVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The address a build can actually be fetched from, or null when the site will not say.
    ///
    /// The token on the end is generated per file and is the whole reason this cannot be a fixed
    /// URL built from the file name.
    /// </summary>
    public async Task<string?> GetDownloadUrlAsync(
        OptiFineBuild build, CancellationToken cancellationToken = default)
    {
        var page = await GetAsync(Site + "adloadx?f=" + Uri.EscapeDataString(build.FileName), cancellationToken)
            .ConfigureAwait(false);

        if (page is null) return null;

        var link = DownloadPattern().Match(page);
        if (!link.Success) return null;

        // Taken from the page rather than rebuilt from its parts, so a change to the query string
        // is carried along instead of being dropped.
        return Site + link.Value.Replace("&amp;", "&", StringComparison.Ordinal);
    }

    private async Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", Browser);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls a file name apart. "OptiFine_1.20.1_HD_U_I6_pre3.jar" is a version, an edition, a
    /// release and a note that it is not finished.
    /// </summary>
    internal static OptiFineBuild? Describe(string fileName)
    {
        var match = NamePattern().Match(fileName);
        if (!match.Success) return null;

        var tail = match.Groups["tail"].Value;
        var preview = tail.Contains("pre", StringComparison.OrdinalIgnoreCase);

        return new OptiFineBuild(
            fileName,
            match.Groups["mc"].Value,
            match.Groups["edition"].Value,

            // Zero-padded so a plain string comparison orders them: without it "M10" sorts before
            // "M5", and the newest build would be whichever happened to have the smallest number.
            Pad(match.Groups["release"].Value),
            preview);
    }

    /// <summary>"M5" to "M05", so ordering by text orders by build.</summary>
    private static string Pad(string release)
    {
        var letters = new string([.. release.TakeWhile(char.IsAsciiLetter)]);
        var digits = release[letters.Length..];

        return int.TryParse(digits, out var number) ? letters + number.ToString("D3") : release;
    }

    [GeneratedRegex(@"adloadx\?f=(?<file>OptiFine_[^""&]+\.jar)", RegexOptions.IgnoreCase)]
    private static partial Regex FilePattern();

    [GeneratedRegex(@"downloadx\?f=[^""']+", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadPattern();

    [GeneratedRegex(
        @"^OptiFine_(?<mc>[\d.]+)_(?<edition>[A-Z]+(?:_[A-Z])?)_(?<release>[A-Z]\d+)(?<tail>.*)\.jar$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();
}
