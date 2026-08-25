using System.ComponentModel;
using Asobu.Core;
using Asobu.Core.Instances;

namespace Asobu.Core.Tests;

/// <summary>
/// Changing an instance's mod loader.
///
/// A tester reported switching to Forge and still seeing Vanilla, so these cover both halves of
/// what "it changed" has to mean: the screen says so, and it is still true after a restart.
/// </summary>
public class InstanceLoaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-loader-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private InstanceStore Store => new(new AsobuPaths(_root));

    private static Instance Vanilla() =>
        new() { Id = "one", Name = "Test", MinecraftVersion = "1.19.2" };

    /// <summary>
    /// The one the tester hit. Three properties are worked out from Loader and every one of them
    /// is on screen, so a quiet write leaves a card insisting the instance is still Vanilla.
    /// </summary>
    [Fact]
    public void Changing_the_loader_tells_the_screen()
    {
        var instance = Vanilla();
        var announced = new List<string>();

        ((INotifyPropertyChanged)instance).PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");

        instance.Loader = "forge";

        Assert.Contains(nameof(Instance.LoaderName), announced);
        Assert.Contains(nameof(Instance.LoaderLabel), announced);
        Assert.Contains(nameof(Instance.IsModded), announced);
    }

    [Fact]
    public void Changing_the_loader_version_tells_the_screen()
    {
        var instance = Vanilla();
        var announced = new List<string>();

        ((INotifyPropertyChanged)instance).PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");

        instance.LoaderVersion = "43.5.0";

        Assert.Contains(nameof(Instance.LoaderLabel), announced);
    }

    [Fact]
    public void Setting_the_same_loader_again_says_nothing()
    {
        var instance = Vanilla();
        var announced = new List<string>();

        ((INotifyPropertyChanged)instance).PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");

        instance.Loader = "vanilla";

        Assert.Empty(announced);
    }

    [Fact]
    public void The_names_follow_the_loader()
    {
        var instance = Vanilla();
        Assert.Equal("Vanilla", instance.LoaderName);
        Assert.False(instance.IsModded);

        instance.Loader = "forge";
        instance.LoaderVersion = "43.5.0";

        Assert.Equal("Forge", instance.LoaderName);
        Assert.Equal("Forge 43.5.0", instance.LoaderLabel);
        Assert.True(instance.IsModded);
    }

    /// <summary>Spelled as people write it rather than as it is stored.</summary>
    [Fact]
    public void NeoForge_keeps_its_capital()
    {
        var instance = Vanilla();
        instance.Loader = "neoforge";

        Assert.Equal("NeoForge", instance.LoaderName);
    }

    // ---- and it has to survive being written down ----

    [Fact]
    public void The_loader_is_still_there_after_a_reload()
    {
        var store = Store;
        var instance = Vanilla();
        store.Save(instance);

        instance.Loader = "forge";
        instance.LoaderVersion = "43.5.0";
        store.Save(instance);

        // A fresh store, so nothing is answered from memory: this is what a restart would read.
        var reloaded = Store.LoadAll().Single();

        Assert.Equal("forge", reloaded.Loader);
        Assert.Equal("43.5.0", reloaded.LoaderVersion);
        Assert.True(reloaded.IsModded);
    }

    /// <summary>
    /// The store remembers its last read, so a save that did not forget it would hand the next
    /// caller the instance as it used to be — which for a launch means starting the old loader.
    /// </summary>
    [Fact]
    public void A_reader_that_looked_before_the_change_sees_it_after()
    {
        var store = Store;
        store.Save(Vanilla());

        var before = store.LoadAll().Single();
        Assert.Equal("vanilla", before.Loader);

        before.Loader = "forge";
        before.LoaderVersion = "43.5.0";
        store.Save(before);

        Assert.Equal("forge", store.LoadAll().Single().Loader);
    }

    [Fact]
    public void Going_back_to_vanilla_drops_the_version()
    {
        var store = Store;
        var instance = Vanilla();

        instance.Loader = "forge";
        instance.LoaderVersion = "43.5.0";
        store.Save(instance);

        instance.Loader = "vanilla";
        instance.LoaderVersion = null;
        store.Save(instance);

        var reloaded = Store.LoadAll().Single();

        Assert.Equal("vanilla", reloaded.Loader);
        Assert.Null(reloaded.LoaderVersion);
        Assert.False(reloaded.IsModded);
    }
}
