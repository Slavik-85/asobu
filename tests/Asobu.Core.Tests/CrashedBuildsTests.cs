using Asobu.Core;
using Asobu.Core.Instances;

namespace Asobu.Core.Tests;

/// <summary>
/// The note that stops an automatic fix going round in circles.
///
/// Two builds of one mod, both tagged for this Minecraft version and neither actually running on
/// it, had the fix swapping from one to the other and back for as long as anybody kept pressing.
/// The way out is remembering which builds have already crashed — and remembering it on disk,
/// because every crash starts a new session and an in-memory note would be gone by the time it
/// was needed.
/// </summary>
public class CrashedBuildsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-crashed-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private InstanceStore Store() => new(new AsobuPaths(_root));

    private static Instance Fresh() => new()
    {
        Id = "abc",
        Name = "Horror Reborn",
        MinecraftVersion = "1.21.8",
        Loader = "fabric",
    };

    [Fact]
    public void ANewInstanceHasCrashedNothing()
    {
        Assert.Empty(Fresh().CrashedBuilds);
    }

    [Fact]
    public void SurvivesBeingWrittenAndReadBack()
    {
        // The load-bearing half. Written during one session and read during the next, because the
        // crash it exists to remember happens in between.
        var store = Store();
        var instance = Fresh();

        instance.CrashedBuilds.Add("corner-entity-2.0.0+1.21.1.jar");
        store.Save(instance);

        var reloaded = Store().LoadAll().Single();

        Assert.Equal(["corner-entity-2.0.0+1.21.1.jar"], reloaded.CrashedBuilds);
    }

    [Fact]
    public void GathersEveryBuildThatCrashed()
    {
        // The exact sequence out of the log: 2.0.0 crashed, was swapped for 1.0.0, which crashed
        // too. With both written down there is nothing left to swap to, and the mod gets turned
        // off instead of the pair being tried again forever.
        var store = Store();
        var instance = Fresh();

        foreach (var build in new[] { "corner-entity-2.0.0+1.21.1.jar", "corner-entity-1.0.0+1.21.1.jar" })
        {
            instance.CrashedBuilds.Add(build);
            store.Save(instance);
        }

        var tried = Store().LoadAll().Single().CrashedBuilds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, tried.Count);
        Assert.Contains("corner-entity-1.0.0+1.21.1.jar", tried);

        // And the one it just swapped back to is recognised whatever case the shop spells it in.
        Assert.Contains("CORNER-ENTITY-2.0.0+1.21.1.JAR", tried);
    }

    [Fact]
    public void ReadsAnInstanceSavedBeforeThisExisted()
    {
        // Every instance.json already on disk predates the field. A missing list has to load as
        // an empty one rather than null, or the first crash after an update throws instead of
        // being fixed.
        var folder = Path.Combine(_root, "instances", "old");
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, "instance.json"), """
            { "id": "old", "name": "Old", "minecraftVersion": "1.21.8", "loader": "fabric" }
            """);

        Assert.Empty(Store().LoadAll().Single().CrashedBuilds);
    }
}
