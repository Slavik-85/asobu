using System.IO.Compression;
using System.Text.Json;
using Asobu.Core;
using Asobu.Core.Instances;

namespace Asobu.Core.Tests;

/// <summary>
/// The shape an exported instance goes out in.
///
/// It used to be the instance folder zipped as it sat — instance.json beside minecraft/ — which
/// is a layout only Asobu has ever heard of, so an exported instance could be handed to nobody.
/// It is CurseForge's layout now, which is the one Prism, MultiMC, ATLauncher, the CurseForge app
/// and Modrinth's all read: a manifest saying which Minecraft and which loader, and an overrides
/// folder holding the files.
///
/// What is checked here is the contract other launchers rely on. Getting the manifest subtly
/// wrong is the whole failure — a pack that will not import reports nothing useful about why.
/// </summary>
public class ExportFormatTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-export-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AsobuPaths Paths => new(_root);
    private InstanceStore Store => new(Paths);

    private Instance Given(string loader = "forge", string? loaderVersion = "43.5.0")
    {
        var instance = Store.Create("Test Pack", "1.19.2", loader, loaderVersion);
        var game = Paths.InstanceGameDir(instance.Folder);

        Directory.CreateDirectory(Path.Combine(game, "mods"));
        Directory.CreateDirectory(Path.Combine(game, "config"));
        Directory.CreateDirectory(Path.Combine(game, "logs"));
        Directory.CreateDirectory(Path.Combine(game, "crash-reports"));

        File.WriteAllText(Path.Combine(game, "mods", "cool.jar"), "jar");
        File.WriteAllText(Path.Combine(game, "config", "cool.toml"), "setting = 1");
        File.WriteAllText(Path.Combine(game, "options.txt"), "fov:80");
        File.WriteAllText(Path.Combine(game, "logs", "latest.log"), "noise");
        File.WriteAllText(Path.Combine(game, "crash-reports", "crash.txt"), "noise");
        File.WriteAllText(Path.Combine(game, "hs_err_pid42.log"), "noise");

        return instance;
    }

    private string Export(Instance instance)
    {
        var zip = Path.Combine(_root, instance.Folder + ".zip");
        Store.Export(instance, zip);
        return zip;
    }

    private static JsonElement Manifest(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());

        return JsonDocument.Parse(reader.ReadToEnd()).RootElement.Clone();
    }

    private static List<string> Names(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return [.. archive.Entries.Select(e => e.FullName)];
    }

    // ---- what other launchers read ----

    [Fact]
    public void There_is_a_manifest_where_every_launcher_looks_for_one()
    {
        var manifest = Manifest(Export(Given()));

        Assert.Equal("minecraftModpack", manifest.GetProperty("manifestType").GetString());
        Assert.Equal(1, manifest.GetProperty("manifestVersion").GetInt32());
        Assert.Equal("overrides", manifest.GetProperty("overrides").GetString());
        Assert.Equal("Test Pack", manifest.GetProperty("name").GetString());
    }

    [Fact]
    public void It_names_the_minecraft_version()
    {
        Assert.Equal("1.19.2", Manifest(Export(Given())).GetProperty("minecraft").GetProperty("version").GetString());
    }

    /// <summary>
    /// "forge-43.5.0" is the spelling both CurseForge and Asobu's own reader expect. A family
    /// name it does not know is a pack that refuses to import, naming a loader nobody has.
    /// </summary>
    [Theory]
    [InlineData("forge", "43.5.0", "forge-43.5.0")]
    [InlineData("fabric", "0.16.9", "fabric-0.16.9")]
    [InlineData("neoforge", "21.1.72", "neoforge-21.1.72")]
    [InlineData("quilt", "0.26.0", "quilt-0.26.0")]
    public void It_names_the_loader_the_way_the_format_does(string loader, string version, string expected)
    {
        var loaders = Manifest(Export(Given(loader, version))).GetProperty("minecraft").GetProperty("modLoaders");

        Assert.Equal(expected, loaders[0].GetProperty("id").GetString());
        Assert.True(loaders[0].GetProperty("primary").GetBoolean());
    }

    /// <summary>A vanilla instance has no loader, rather than one called "vanilla".</summary>
    [Fact]
    public void A_vanilla_instance_names_no_loader_at_all()
    {
        var manifest = Manifest(Export(Given(Asobu.Core.Minecraft.Loaders.Vanilla, null)));

        Assert.Empty(manifest.GetProperty("minecraft").GetProperty("modLoaders").EnumerateArray());
    }

    /// <summary>
    /// Every mod travels as a jar rather than as a list of ids to go and fetch. Asobu does not
    /// record which catalogue file each jar came from, so a list could not be written honestly —
    /// and a pack carrying its own mods does not go stale when a build is delisted.
    /// </summary>
    [Fact]
    public void The_files_list_is_empty_and_the_mods_are_really_in_there()
    {
        var zip = Export(Given());

        Assert.Empty(Manifest(zip).GetProperty("files").EnumerateArray());
        Assert.Contains("overrides/mods/cool.jar", Names(zip));
    }

    [Fact]
    public void The_game_folder_travels_as_overrides()
    {
        var names = Names(Export(Given()));

        Assert.Contains("overrides/options.txt", names);
        Assert.Contains("overrides/config/cool.toml", names);
        Assert.DoesNotContain(names, name => name.StartsWith("minecraft/"));
    }

    /// <summary>Nobody wants a copy of this machine's logs or the reports from its crashes.</summary>
    [Fact]
    public void Logs_and_crash_reports_do_not_travel()
    {
        var names = Names(Export(Given()));

        Assert.DoesNotContain(names, name => name.StartsWith("overrides/logs/"));
        Assert.DoesNotContain(names, name => name.StartsWith("overrides/crash-reports/"));
        Assert.DoesNotContain(names, name => name.Contains("hs_err_pid"));
    }

    // ---- and what Asobu gets back ----

    /// <summary>
    /// Asobu's own file rides at the root, where other launchers ignore it. It is what carries
    /// the things a manifest has no room for, and it has to be at the top level for the import
    /// to find it before it reaches the manifest.
    /// </summary>
    [Fact]
    public void Asobus_own_instance_file_rides_along_at_the_root()
    {
        Assert.Contains("instance.json", Names(Export(Given())));
    }

    /// <summary>The tile and the banner sit beside instance.json, not inside the game folder.</summary>
    [Fact]
    public void A_custom_icon_and_banner_travel_too()
    {
        var instance = Given();

        var picture = Path.Combine(_root, "source.png");
        File.WriteAllText(picture, "png");

        Store.SetCustomIcon(instance, picture);
        Store.SetCustomBanner(instance, picture);

        var names = Names(Export(instance));

        Assert.Contains("icon.png", names);
        Assert.Contains("banner.png", names);
    }
}
