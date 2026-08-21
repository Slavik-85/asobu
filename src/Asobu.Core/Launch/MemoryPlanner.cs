using Asobu.Core.Instances;
using Asobu.Core.Mods;

namespace Asobu.Core.Launch;

/// <summary>
/// Picks a heap size for one instance instead of using a single number for all of them.
///
/// More memory is not better. Java only collects garbage when it has to, so an oversized heap
/// trades frequent short pauses for rare long ones — the stutter people blame on their PC. And
/// every megabyte handed to the JVM is one the OS can't spend on the file cache the game reads
/// its chunks and textures through. So the figures below are sized to what a pack actually
/// touches, then capped by what the machine can spare.
/// </summary>
public static class MemoryPlanner
{
    private const int StepMb = 512;

    /// <summary>What vanilla wants. Mojang's own launcher ships the same figure.</summary>
    private const int VanillaMb = 2048;

    /// <summary>Below this the game is not reliably playable, so it is the floor everywhere.</summary>
    private const int FloorMb = 1024;

    public static int MaxMemoryMbFor(AsobuPaths paths, Instance instance) =>
        Round(Math.Clamp(ForModCount(CountMods(paths, instance)), FloorMb, Ceiling()));

    /// <summary>
    /// -Xms. Deliberately well under the maximum: starting small lets the JVM grow to what the
    /// pack really needs, and a heap that starts at its ceiling never gets the chance.
    /// </summary>
    public static int MinMemoryMbFor(int maxMemoryMb) =>
        Round(Math.Clamp(maxMemoryMb / 4, 512, 1024));

    /// <summary>How much a single "give it more" is worth. Two gigabytes is a step somebody notices.</summary>
    private const int RaiseMb = 2048;

    /// <summary>
    /// What this instance's ceiling should become after it ran out of memory, or null when there
    /// is no room left to give.
    ///
    /// Null rather than the number it is already on, so a crash the launcher cannot do anything
    /// about does not get offered a button that would change nothing. Out of memory at the
    /// machine's own limit is a different problem, and pretending otherwise wastes a click.
    /// </summary>
    public static int? RaisedFor(AsobuPaths paths, Instance instance)
    {
        var current = instance.MaxMemoryMb ?? MaxMemoryMbFor(paths, instance);
        var ceiling = Ceiling();

        return current >= ceiling ? null : Math.Min(Round(current + RaiseMb), ceiling);
    }

    /// <summary>What an instance is set to run on now, whether it says so itself or leaves it to Asobu.</summary>
    public static int CurrentMaxMemoryMb(AsobuPaths paths, Instance instance) =>
        instance.MaxMemoryMb ?? MaxMemoryMbFor(paths, instance);

    /// <summary>How many enabled jars are in the instance's mods folder.</summary>
    public static int CountMods(AsobuPaths paths, Instance instance)
    {
        var directory = ModScanner.ModsDirectory(paths, instance.Folder);
        if (!Directory.Exists(directory)) return 0;

        try
        {
            // Asobu disables a mod by renaming it to .jar.disabled, so "*.jar" counts exactly
            // the ones that will actually load.
            return Directory.EnumerateFiles(directory, "*.jar").Count();
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static int ForModCount(int mods) => mods switch
    {
        0 => VanillaMb,
        <= 20 => 3072,   // a handful of quality-of-life mods
        <= 60 => 4096,   // a light pack
        <= 150 => 6144,  // a full pack, world generation or shaders
        _ => 8192,       // kitchen sink
    };

    /// <summary>
    /// Leave the machine its own room: half of it at most, and never within 2 GB of the total.
    /// A heap big enough to push the system into swapping is slower than a small one.
    /// </summary>
    private static int Ceiling()
    {
        var systemMb = LauncherSettings.SystemMemoryMb();
        return Math.Max(FloorMb, Math.Min(systemMb / 2, systemMb - 2048));
    }

    private static int Round(int megabytes) => Math.Max(StepMb, megabytes / StepMb * StepMb);
}
