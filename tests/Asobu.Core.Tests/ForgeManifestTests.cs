using System.IO.Compression;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// Reading a Forge or NeoForge mod's own manifest.
///
/// Without this every one of them showed as its own file name with no author, sitting in the list
/// beside properly named Fabric mods as though one of the two were broken.
/// </summary>
public class ForgeManifestTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("asobu-forge-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Jar(string name, string manifestPath, string toml, byte[]? logo = null)
    {
        var path = Path.Combine(_folder, name);

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        using (var writer = new StreamWriter(zip.CreateEntry(manifestPath).Open()))
            writer.Write(toml);

        if (logo is not null)
        {
            using var stream = zip.CreateEntry("logo.png").Open();
            stream.Write(logo);
        }

        return path;
    }

    private ModEntry Only() => Assert.Single(ModScanner.Scan(_folder, ModKind.Mod));

    /// <summary>
    /// The case that was broken in practice. Forge's own template puts a comment after almost
    /// every line, so a value hardly ever ends at its closing quote — and reading those as
    /// unquoted left the quotation marks in the mod's name on screen.
    /// </summary>
    [Fact]
    public void Reads_a_value_with_a_comment_after_it()
    {
        Jar("deimos-1.21.8-forge-2.7.jar", "META-INF/mods.toml", """
            modLoader="javafml" #mandatory
            [[mods]]
            modId = "deimos" #mandatory
            displayName = "Deimos" #mandatory
            authors = "Mars" #optional
            """);

        var mod = Only();

        Assert.Equal("Deimos", mod.Name);
        Assert.Equal("Mars", mod.Author);
        Assert.Equal("deimos", mod.ModId);
    }

    [Fact]
    public void Reads_neoforges_own_file_name()
    {
        Jar("thing-1.0.jar", "META-INF/neoforge.mods.toml", """
            [[mods]]
            modId="thing"
            displayName="The Thing"
            authors="Someone"
            """);

        Assert.Equal("The Thing", Only().Name);
    }

    /// <summary>A pack of several mods is one file, and it is named after the first.</summary>
    [Fact]
    public void Takes_the_first_mod_of_several()
    {
        Jar("bundle-1.0.jar", "META-INF/mods.toml", """
            [[mods]]
            modId="first"
            displayName="The First"
            [[mods]]
            modId="second"
            displayName="The Second"
            """);

        Assert.Equal("The First", Only().Name);
    }

    /// <summary>Shared details sit above the blocks; a mod's own win where both say something.</summary>
    [Fact]
    public void Falls_back_to_the_top_level_for_what_a_mod_does_not_say()
    {
        Jar("thing-1.0.jar", "META-INF/mods.toml", """
            authors="The Team"
            [[mods]]
            modId="thing"
            displayName="Thing"
            """);

        Assert.Equal("The Team", Only().Author);
    }

    /// <summary>A description is written across several lines and is not something to show.</summary>
    [Fact]
    public void Does_not_choke_on_a_multi_line_string()
    {
        Jar("thing-1.0.jar", "META-INF/mods.toml", """
            [[mods]]
            modId="thing"
            displayName="Thing"
            description='''
            Several lines
            of prose.
            '''
            authors="Someone"
            """);

        var mod = Only();

        Assert.Equal("Thing", mod.Name);
        Assert.Equal("Someone", mod.Author);
    }

    [Fact]
    public void Finds_the_logo_a_manifest_names()
    {
        Jar("thing-1.0.jar", "META-INF/mods.toml", """
            [[mods]]
            modId="thing"
            displayName="Thing"
            logoFile = "logo.png" #optional
            """, logo: [1, 2, 3, 4]);

        Assert.Equal(4, Only().IconPng?.Length);
    }

    /// <summary>No manifest at all still belongs in the list, under the only name there is.</summary>
    [Fact]
    public void A_jar_with_no_manifest_keeps_its_file_name()
    {
        var path = Path.Combine(_folder, "mystery-1.0.jar");
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            zip.CreateEntry("nothing.txt");

        var mod = Only();

        Assert.Equal("Unknown", mod.Author);
        Assert.Null(mod.ModId);
    }
}
