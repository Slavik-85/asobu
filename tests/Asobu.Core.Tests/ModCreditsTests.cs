using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// Names kept from the catalogue a mod was downloaded from.
///
/// The launcher knew what the project was called at the moment it fetched the file and then threw
/// it away, so a jar that carries no name of its own showed as its file name. This keeps it — but
/// only ever as a fallback: a mod that names itself is the authority on its own name.
/// </summary>
public class ModCreditsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-credits-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AsobuPaths Paths => new(_root);

    private Instance MakeInstance(string folder = "one")
    {
        var instance = new Instance { Id = "x", Name = "Test", MinecraftVersion = "1.21" };
        instance.Folder = folder;
        Directory.CreateDirectory(Paths.InstanceDir(folder));
        return instance;
    }

    private static ModEntry Scanned(string file, string name, string author, bool declared) =>
        new($"C:/mods/{file}", file, name, author, null, 1024, true, null) { Declared = declared };

    [Fact]
    public void Dresses_a_jar_that_never_named_itself()
    {
        var instance = MakeInstance();
        ModCredits.Record(Paths, instance, "Essential_1-4-1-1_fabric.jar",
            new ModCredit("Essential", "Essential Mod", "Modrinth"));

        var dressed = ModCredits.For(Paths, instance)
            .Dress(Scanned("Essential_1-4-1-1_fabric.jar", "Essential", "Unknown", declared: false));

        Assert.Equal("Essential", dressed.Name);
        Assert.Equal("Essential Mod", dressed.Author);
    }

    /// <summary>
    /// A mod that names itself is the authority on its own name. Shops and authors disagree about
    /// capitals and suffixes all the time, and the author is the one who wrote it.
    /// </summary>
    [Fact]
    public void Leaves_a_declared_name_alone()
    {
        var instance = MakeInstance();
        ModCredits.Record(Paths, instance, "sodium.jar", new ModCredit("Sodium (Fabric)", "shop", "Modrinth"));

        var dressed = ModCredits.For(Paths, instance)
            .Dress(Scanned("sodium.jar", "Sodium", "JellySquid", declared: true));

        Assert.Equal("Sodium", dressed.Name);
        Assert.Equal("JellySquid", dressed.Author);
    }

    /// <summary>An author is filled in even where the name was declared — the two are separate.</summary>
    [Fact]
    public void Fills_in_only_a_missing_author()
    {
        var instance = MakeInstance();
        ModCredits.Record(Paths, instance, "thing.jar", new ModCredit("The Thing", "Someone", "CurseForge"));

        var dressed = ModCredits.For(Paths, instance)
            .Dress(Scanned("thing.jar", "Thing", "Unknown", declared: true));

        Assert.Equal("Thing", dressed.Name);
        Assert.Equal("Someone", dressed.Author);
    }

    [Fact]
    public void Leaves_a_file_it_knows_nothing_about_untouched()
    {
        var instance = MakeInstance();
        var entry = Scanned("dropped-in-by-hand.jar", "Dropped in by hand", "Unknown", declared: false);

        Assert.Equal(entry, ModCredits.For(Paths, instance).Dress(entry));
    }

    /// <summary>Two instances must never wear each other's names.</summary>
    [Fact]
    public void Keeps_instances_apart()
    {
        var one = MakeInstance("one");
        var two = MakeInstance("two");

        ModCredits.Record(Paths, one, "thing.jar", new ModCredit("The Thing", "Someone", "Modrinth"));

        Assert.Null(ModCredits.For(Paths, two).Get("thing.jar"));
    }

    /// <summary>
    /// An instance with nothing recorded yet comes back as a shared empty. Writing to one must not
    /// put its names into the next instance that asks.
    /// </summary>
    [Fact]
    public void Recording_does_not_leak_through_the_empty_case()
    {
        var one = MakeInstance("one");
        var two = MakeInstance("two");

        _ = ModCredits.For(Paths, two);      // the empty one, handed out first
        ModCredits.Record(Paths, one, "thing.jar", new ModCredit("The Thing", "Someone", "Modrinth"));

        Assert.Null(ModCredits.For(Paths, two).Get("thing.jar"));
        Assert.Null(ModCredits.Empty.Get("thing.jar"));
    }

    [Fact]
    public void Remembers_more_than_one_and_survives_a_reload()
    {
        var instance = MakeInstance();

        ModCredits.Record(Paths, instance, "a.jar", new ModCredit("Ay", "One", "Modrinth"));
        ModCredits.Record(Paths, instance, "b.jar", new ModCredit("Bee", "Two", "CurseForge"));

        var read = ModCredits.For(Paths, instance);

        Assert.Equal("Ay", read.Get("a.jar")?.Name);
        Assert.Equal("Bee", read.Get("b.jar")?.Name);
    }

    /// <summary>The file lives beside instance.json, never inside the folder the game reads.</summary>
    [Fact]
    public void Is_kept_out_of_the_game_folder()
    {
        var instance = MakeInstance();
        ModCredits.Record(Paths, instance, "a.jar", new ModCredit("Ay", "One", "Modrinth"));

        var game = Paths.InstanceGameDir("one");
        if (Directory.Exists(game))
            Assert.Empty(Directory.GetFiles(game, "asobu-mods.json", SearchOption.AllDirectories));

        Assert.True(File.Exists(Path.Combine(Paths.InstanceDir("one"), "asobu-mods.json")));
    }
}
