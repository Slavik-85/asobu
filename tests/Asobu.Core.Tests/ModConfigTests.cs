using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// Reading and writing a mod's own config file.
///
/// The write side is where the care goes. This edits the only copy of somebody's settings, in a
/// file a mod will read at startup and may refuse to load if it is malformed — so what comes back
/// has to be the file that went in, with one value different and everything else, comments
/// included, exactly where it was.
/// </summary>
public class ModConfigTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-modconfig-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string name, string content)
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);

        var path = Path.Combine(config, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ModEntry Mod(string id, string fileName = "whatever.jar") =>
        new(Path.Combine("mods", fileName), fileName, id, "Someone", id, 1024, true, null);

    // ---- finding the file ----

    [Fact]
    public void Finds_a_file_named_after_the_mod_id()
    {
        Write("sodium.json", "{\"quality\": 3}");

        var found = ModConfig.FilesFor(_root, Mod("sodium"));

        Assert.Single(found);
        Assert.EndsWith("sodium.json", found[0]);
    }

    [Fact]
    public void Finds_the_split_files_forge_writes()
    {
        Write("examplemod-common.toml", "greeting = \"hello\"");
        Write("examplemod-client.toml", "fancy = true");

        Assert.Equal(2, ModConfig.FilesFor(_root, Mod("examplemod")).Count);
    }

    [Fact]
    public void Does_not_claim_another_mods_file()
    {
        Write("sodium.json", "{\"quality\": 3}");
        Write("iris.json", "{\"shaders\": true}");

        var found = ModConfig.FilesFor(_root, Mod("sodium"));

        Assert.Single(found);
        Assert.DoesNotContain(found, path => path.Contains("iris"));
    }

    [Fact]
    public void Finds_a_folder_of_files_under_the_mod_id()
    {
        var nested = Path.Combine(_root, "config", "bigmod");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "graphics.toml"), "fog = true");

        var found = ModConfig.FilesFor(_root, Mod("bigmod"));

        Assert.Single(found);
        Assert.EndsWith("graphics.toml", found[0]);
    }

    // ---- reading ----

    [Fact]
    public void Reads_json_including_what_is_nested()
    {
        var path = Write("thing.json", """
            {
              "enabled": true,
              "quality": 3,
              "name": "default",
              "graphics": { "fog": false }
            }
            """);

        var config = ModConfig.Open(path)!;

        Assert.Equal(ConfigValueKind.Boolean, config.Settings.Single(s => s.Key == "enabled").Kind);
        Assert.Equal(ConfigValueKind.Number, config.Settings.Single(s => s.Key == "quality").Kind);
        Assert.Equal(ConfigValueKind.Text, config.Settings.Single(s => s.Key == "name").Kind);

        var nested = config.Settings.Single(s => s.Key == "graphics.fog");
        Assert.Equal("graphics", nested.Section);
        Assert.Equal("false", nested.Value);
    }

    [Fact]
    public void Reads_toml_with_its_sections_and_comments()
    {
        var path = Write("thing.toml", """
            # Whether the thing is on
            enabled = true

            [graphics]
            # How much fog to draw
            fog = 0.5
            preset = "fancy"
            """);

        var config = ModConfig.Open(path)!;

        Assert.Equal("Whether the thing is on", config.Settings.Single(s => s.Key == "enabled").Note);

        var fog = config.Settings.Single(s => s.Key == "graphics.fog");
        Assert.Equal("How much fog to draw", fog.Note);
        Assert.Equal("0.5", fog.Value);

        Assert.Equal("fancy", config.Settings.Single(s => s.Key == "graphics.preset").Value);
    }

    [Fact]
    public void Leaves_lists_alone()
    {
        var path = Write("thing.toml", """
            keys = ["a", "b"]
            simple = true
            """);

        var setting = Assert.Single(ModConfig.Open(path)!.Settings);
        Assert.Equal("simple", setting.Key);
    }

    [Fact]
    public void Ignores_a_trailing_comment_on_a_value()
    {
        var path = Write("thing.toml", "quality = 3 # higher is slower");

        Assert.Equal("3", ModConfig.Open(path)!.Settings.Single().Value);
    }

    // ---- writing ----

    [Fact]
    public void Writes_toml_back_with_its_comments_intact()
    {
        var original = """
            # Whether the thing is on
            enabled = true

            [graphics]
            # How much fog to draw
            fog = 0.5
            preset = "fancy"
            """;
        var path = Write("thing.toml", original);

        ModConfig.Open(path)!.Save(new Dictionary<string, string>
        {
            ["enabled"] = "false",
            ["graphics.preset"] = "fast",
        });

        var after = File.ReadAllText(path);

        Assert.Contains("# Whether the thing is on", after);
        Assert.Contains("# How much fog to draw", after);
        Assert.Contains("enabled = false", after);
        Assert.Contains("preset = \"fast\"", after);
        Assert.Contains("fog = 0.5", after);      // untouched
        Assert.Contains("[graphics]", after);
    }

    /// <summary>A quoted value must stay quoted and an unquoted one must stay bare.</summary>
    [Fact]
    public void Keeps_the_quoting_a_value_had()
    {
        var path = Write("thing.toml", """
            name = "old"
            count = 1
            """);

        ModConfig.Open(path)!.Save(new Dictionary<string, string>
        {
            ["name"] = "new",
            ["count"] = "2",
        });

        var after = File.ReadAllText(path);

        Assert.Contains("name = \"new\"", after);
        Assert.Contains("count = 2", after);
        Assert.DoesNotContain("count = \"2\"", after);
    }

    /// <summary>Two sections with the same key name must not be confused for each other.</summary>
    [Fact]
    public void Writes_the_right_one_of_two_identical_names()
    {
        var path = Write("thing.toml", """
            [client]
            enabled = true

            [server]
            enabled = true
            """);

        ModConfig.Open(path)!.Save(new Dictionary<string, string> { ["server.enabled"] = "false" });

        var after = File.ReadAllText(path);
        var client = after.IndexOf("[client]", StringComparison.Ordinal);
        var server = after.IndexOf("[server]", StringComparison.Ordinal);

        Assert.Contains("enabled = true", after[client..server]);
        Assert.Contains("enabled = false", after[server..]);
    }

    [Fact]
    public void Writes_json_back_with_its_types()
    {
        var path = Write("thing.json", """
            { "enabled": true, "quality": 3, "name": "default" }
            """);

        ModConfig.Open(path)!.Save(new Dictionary<string, string>
        {
            ["enabled"] = "false",
            ["quality"] = "5",
            ["name"] = "custom",
        });

        var after = ModConfig.Open(path)!;

        Assert.Equal("false", after.Settings.Single(s => s.Key == "enabled").Value);
        Assert.Equal("5", after.Settings.Single(s => s.Key == "quality").Value);
        Assert.Equal("custom", after.Settings.Single(s => s.Key == "name").Value);

        // And the types survived rather than everything becoming a string.
        var raw = File.ReadAllText(path);
        Assert.Contains("\"enabled\": false", raw);
        Assert.Contains("\"quality\": 5", raw);
        Assert.Contains("\"name\": \"custom\"", raw);
    }

    [Fact]
    public void Writes_nested_json_in_the_right_place()
    {
        var path = Write("thing.json", """
            { "graphics": { "fog": true }, "fog": false }
            """);

        ModConfig.Open(path)!.Save(new Dictionary<string, string> { ["graphics.fog"] = "false" });

        var after = ModConfig.Open(path)!;

        Assert.Equal("false", after.Settings.Single(s => s.Key == "graphics.fog").Value);
        Assert.Equal("false", after.Settings.Single(s => s.Key == "fog").Value);
    }

    [Fact]
    public void Changing_nothing_leaves_the_file_byte_for_byte()
    {
        var original = "# a comment\nenabled = true\n";
        var path = Write("thing.toml", original);

        ModConfig.Open(path)!.Save(new Dictionary<string, string>());

        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void A_file_that_is_not_config_is_not_offered()
    {
        Assert.Null(ModConfig.Open(Write("thing.toml", "just some prose, no settings here")));
    }

    [Fact]
    public void Reads_properties_files()
    {
        var path = Write("thing.properties", """
            # the greeting
            greeting=hello
            count=4
            """);

        var config = ModConfig.Open(path)!;

        Assert.Equal("hello", config.Settings.Single(s => s.Key == "greeting").Value);
        Assert.Equal(ConfigValueKind.Number, config.Settings.Single(s => s.Key == "count").Kind);
    }

    [Fact]
    public void Turns_a_key_into_something_readable()
    {
        var path = Write("thing.toml", "enableFancyFog = true");

        Assert.Equal("Enable fancy fog", ModConfig.Open(path)!.Settings.Single().Label);
    }
}
