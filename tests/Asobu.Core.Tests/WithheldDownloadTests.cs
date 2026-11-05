using System.Net;
using System.Security.Cryptography;
using Asobu.Core;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// A file whose author lets only CurseForge's own page serve it. Modrinth having the very same
/// file means no browser at all; anything going wrong there only means falling back on it.
/// </summary>
public sealed class WithheldDownloadTests : IDisposable
{
    private const string Page = "https://www.curseforge.com/minecraft/mc-mods/test-mod";
    private const string Cdn = "https://cdn.modrinth.com/data/x/other-name.jar";

    private static readonly byte[] Jar = "pretend this is a mod"u8.ToArray();
    private static readonly string Sha1 = Convert.ToHexStringLower(SHA1.HashData(Jar));

    private readonly string _root = Directory.CreateTempSubdirectory("asobu-withheld-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Both shops, answering the way they really do. Anything unexpected is a 404.</summary>
    private sealed class Shops : HttpMessageHandler
    {
        public Func<HttpResponseMessage> ModrinthLookup { get; set; } = () => Json($$$"""
            {"{{{Sha1}}}":{"version_type":"release","files":[
              {"url":"{{{Cdn}}}","filename":"other-name.jar","primary":true,"size":{{{Jar.Length}}},"hashes":{"sha1":"{{{Sha1}}}"}}]}}
            """);

        public byte[] CdnBytes { get; set; } = Jar;

        public string? Fingerprints { get; set; }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            if (url.Contains("api.curseforge.com/v1/mods/1/files"))
                return Json($$"""
                    {"data":[{"id":7,"modId":1,"fileName":"test-mod-2.0.jar","fileLength":{{Jar.Length}},"releaseType":1,
                      "downloadUrl":null,"fileDate":"2026-01-01T00:00:00Z","hashes":[{"value":"{{Sha1}}","algo":1}]}]}
                    """);

            if (url.EndsWith("/v1/fingerprints") && Fingerprints is { } prints) return Json(prints);

            // Asked first about what is installed, which it has never heard of.
            if (url.EndsWith("/v2/version_files")) return body.Contains(Sha1) ? ModrinthLookup() : Json("{}");
            if (url.EndsWith("/v2/version_files/update")) return Json("{}");

            if (url == Cdn) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(CdnBytes) };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private (AsobuLauncher Launcher, Instances.Instance Instance) Set(Shops shops)
    {
        var launcher = new AsobuLauncher(new HttpClient(shops), new AsobuPaths(_root));
        launcher.Settings.CurseForgeApiKey = "test";

        return (launcher, launcher.Instances.Create("Test", "1.20.1", "forge", "47.3.0"));
    }

    private async Task<(ModInstallResult Result, int BrowserAsked, string Mods)> Install(Shops shops)
    {
        var (launcher, instance) = Set(shops);

        var entry = new CatalogueMod(null, new ModListing(ModProvider.CurseForge, "1", "Test Mod", "", "", null, 1, Page));

        var browserAsked = 0;
        var result = await launcher.InstallModAsync(instance, entry, item =>
        {
            Assert.Equal(Page + "/download/7", item.PageUrl);
            browserAsked++;
            return Task.FromResult(false);
        });

        return (result, browserAsked, ModScanner.ModsDirectory(launcher.Paths, instance.Folder));
    }

    [Fact]
    public async Task The_same_file_on_Modrinth_goes_in_without_the_browser()
    {
        var (result, browserAsked, mods) = await Install(new Shops());

        Assert.Equal(0, browserAsked);
        Assert.Equal(ModProvider.Modrinth, result.From);
        Assert.Equal(Jar, await File.ReadAllBytesAsync(Path.Combine(mods, "test-mod-2.0.jar")));
    }

    [Fact]
    public async Task A_file_Modrinth_does_not_have_goes_to_the_browser()
    {
        var (result, browserAsked, _) = await Install(new Shops
        {
            ModrinthLookup = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") },
        });

        Assert.Equal(1, browserAsked);
        Assert.True(result.Blocked);
    }

    [Fact]
    public async Task Modrinth_not_answering_goes_to_the_browser()
    {
        var (result, browserAsked, _) = await Install(new Shops
        {
            // What HttpClient throws when its own timeout runs out.
            ModrinthLookup = () => throw new TaskCanceledException("timed out"),
        });

        Assert.Equal(1, browserAsked);
        Assert.True(result.Blocked);
    }

    [Fact]
    public async Task Bytes_that_fail_the_hash_go_to_the_browser_and_nothing_goes_in()
    {
        var (result, browserAsked, mods) = await Install(new Shops { CdnBytes = "something else entirely"u8.ToArray() });

        Assert.Equal(1, browserAsked);
        Assert.True(result.Blocked);
        Assert.False(Directory.Exists(mods) && Directory.EnumerateFiles(mods).Any());
    }

    /// <summary>A row with no address offers no button, so it has to be found with the update.</summary>
    [Fact]
    public async Task A_withheld_update_on_Modrinth_can_be_applied()
    {
        var shops = new Shops();
        var (launcher, instance) = Set(shops);

        var mods = ModScanner.ModsDirectory(launcher.Paths, instance.Folder);
        Directory.CreateDirectory(mods);

        var installed = Path.Combine(mods, "test-mod-1.0.jar");
        await File.WriteAllBytesAsync(installed, "the old build"u8.ToArray());

        var print = await CurseForgeFingerprint.OfFileAsync(installed);
        shops.Fingerprints = $$$"""
            {"data":{"exactMatches":[{"file":{"id":6,"modId":1,"fileFingerprint":{{{print}}},"releaseType":1,"fileDate":"2025-01-01T00:00:00Z"}}]}}
            """;

        var updates = await launcher.FindUpdatesAsync(
            instance, [new ModEntry(installed, "test-mod-1.0.jar", "Test Mod", "", null, 1, true, null)]);

        var update = Assert.Single(updates);
        Assert.True(update.CanApply);
        Assert.Equal(Cdn, update.Url);
    }
}
