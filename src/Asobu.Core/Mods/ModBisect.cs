namespace Asobu.Core.Mods;

/// <summary>
/// What a bisect has decided so far, and what it wants turned off for the next launch.
/// </summary>
/// <param name="Restore">
/// Every mod that was switched on when this started. Kept so the folder can be put back exactly
/// as it was, whatever happens in between — this is the one field that must survive everything,
/// because without it a bisect that is abandoned halfway leaves somebody with half a modpack and
/// no record of which half.
/// </param>
/// <param name="Suspects">The mods that could still be the one. When this is down to one, that is the answer.</param>
/// <param name="Off">What is switched off for the launch about to happen.</param>
/// <param name="Launches">How many launches this has taken, for saying so on screen.</param>
/// <param name="Crashes">
/// How many of those launches actually crashed.
///
/// Worth counting because of what it means when it is zero. Halving is only sound while the
/// crash is reproducible, and a hunt where the crash never came back cannot tell "switching those
/// off fixed it" from "it did not happen to go wrong this time" — it will still halve its way to
/// a single mod, and that mod will be innocent. The count is what lets the answer be given with
/// the doubt it deserves instead of as a finding.
/// </param>
public sealed record BisectState(
    IReadOnlyList<string> Restore,
    IReadOnlyList<string> Suspects,
    IReadOnlyList<string> Off,
    int Launches,
    int Crashes = 0)
{
    /// <summary>Roughly how many launches are left, which is the only honest thing to promise.</summary>
    public int Remaining => Suspects.Count <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(Suspects.Count));

    /// <summary>
    /// Whether the crash was ever seen again after the first launch settled that a mod was doing
    /// it. False means every step since was a clean launch, which narrows just as fast and proves
    /// nothing.
    /// </summary>
    public bool Reproduced => Crashes > 0;
}

/// <summary>
/// What switching the folder over actually managed.
/// </summary>
/// <param name="Changed">How many jars were renamed.</param>
/// <param name="Refused">
/// The ones that could not be, and why it matters: a hunt is only safe while the folder can be
/// put back, so a jar something else is holding open has to be said out loud rather than left to
/// be discovered as a mod that never came back.
/// </param>
public sealed record BisectChange(int Changed, IReadOnlyList<string> Refused)
{
    public bool Complete => Refused.Count == 0;
}

/// <summary>What the last launch settled.</summary>
public enum BisectVerdict
{
    /// <summary>Not finished. <see cref="ModBisect.Next"/> has handed back the next thing to try.</summary>
    Narrowing,

    /// <summary>One mod left, and it is the one.</summary>
    Found,

    /// <summary>
    /// It crashed with every suspect switched off, so no mod is doing it. Worth its own answer:
    /// the alternative is narrowing down to some innocent mod and being believed.
    /// </summary>
    NotAMod,
}

/// <summary>
/// Finding the mod by halving, when reading the crash report has not found it.
///
/// Asobu's ordinary answer is to read the report and name what it points at, which works whenever
/// the report points at anything. Plenty do not: the process died without writing one, the stack
/// is all loader code, or two mods are fine apart and fatal together and the trace names whichever
/// happened to be running. The report has nothing in it to be right about.
///
/// So stop reading and start measuring. Switch off half the mods, launch, and see. Still crashes:
/// it is one of the half still on. Does not crash: it is one of the half switched off. Either way
/// half the suspects are gone, and half of what is left goes next time — about seven launches for
/// a hundred mods, and it does not need to understand the crash at all.
///
/// Nothing here touches a file. It is the arithmetic of which half to try, kept apart from the
/// business of renaming jars so that the decisions can be tested without a mods folder and so that
/// a bisect interrupted between two launches is a record rather than a state of the disk.
/// </summary>
public static class ModBisect
{
    /// <summary>
    /// The first thing to try: everything off.
    ///
    /// Not a wasted launch. It answers the question the whole hunt assumes the answer to — that a
    /// mod is doing this — and the hunt cannot ask it later. A graphics driver or a full page file
    /// crashes just as reliably with every mod switched off, and a bisect that never checks will
    /// still finish, having halved its way to whichever mod was last in the list.
    /// </summary>
    public static BisectState Begin(IEnumerable<string> enabled)
    {
        var mods = Clean(enabled);

        return new BisectState(mods, mods, mods, 0);
    }

    /// <summary>
    /// What the outcome of a launch means, and what to try next.
    ///
    /// <paramref name="crashed"/> is the only input, because it is the only thing that can be
    /// known without understanding the crash.
    /// </summary>
    public static (BisectVerdict Verdict, BisectState State) Next(BisectState state, bool crashed)
    {
        var launched = state with
        {
            Launches = state.Launches + 1,
            Crashes = state.Crashes + (crashed ? 1 : 0),
        };

        // Everything that could be the culprit was off, and it crashed anyway.
        if (crashed && state.Off.Count == state.Suspects.Count && state.Suspects.Count > 0)
            return (BisectVerdict.NotAMod, launched with { Off = [] });

        // Whichever half the answer is in. Crashed with these off means the answer was among the
        // ones left on; a clean launch means it was among the ones taken away.
        var narrowed = crashed
            ? state.Suspects.Where(mod => !state.Off.Contains(mod, StringComparer.OrdinalIgnoreCase)).ToList()
            : [.. state.Off];

        // Nothing left to suspect. A clean launch with nothing off, or a set that cannot be split
        // any further — either way there is no mod to name, and saying so beats naming one anyway.
        if (narrowed.Count == 0) return (BisectVerdict.NotAMod, launched with { Suspects = [], Off = [] });

        // Found, and left switched off. Everything else goes back on, so the instance ends the
        // hunt in the state somebody actually wants: playable, with the one mod out of it.
        if (narrowed.Count == 1)
            return (BisectVerdict.Found, launched with { Suspects = narrowed, Off = narrowed });

        return (BisectVerdict.Narrowing, launched with { Suspects = narrowed, Off = Half(narrowed) });
    }

    /// <summary>The mod it found, once the verdict is <see cref="BisectVerdict.Found"/>.</summary>
    public static string? Culprit(BisectState state) => state.Suspects.Count == 1 ? state.Suspects[0] : null;

    /// <summary>
    /// Which of the mods on disk should be switched on for the launch this state describes, out of
    /// everything that was on when the bisect started.
    ///
    /// Worked out from the original list every time rather than by tracking what was changed last,
    /// so a step that was interrupted, repeated, or applied twice ends in the same place.
    /// </summary>
    public static bool ShouldBeOn(BisectState state, string fileName) =>
        state.Restore.Contains(fileName, StringComparer.OrdinalIgnoreCase)
        && !state.Off.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Switches the mods folder to what a state wants, and says how many jars were renamed.
    ///
    /// The only part of this that touches a disk. Every mod is compared against what the state
    /// says it should be rather than against what the last step did, so applying a state twice,
    /// or applying one after the app was closed mid-hunt, lands in the same place.
    /// </summary>
    public static BisectChange Apply(BisectState state, IReadOnlyList<ModEntry> mods)
    {
        var changed = 0;
        var refused = new List<string>();

        // A jar and its own .disabled twin, both sitting in the folder. The scanner strips the
        // suffix, so they come back as two entries under one name, and renaming either onto the
        // other is a file destroyed. Neither is touched.
        var twinned = mods
            .GroupBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            // Not part of this hunt. Either it was already switched off before any of this, or
            // somebody installed it since — and a mod the hunt never switched off is not a mod
            // the hunt gets to have an opinion about.
            if (!state.Restore.Contains(mod.FileName, StringComparer.OrdinalIgnoreCase)) continue;

            if (twinned.Contains(mod.FileName))
            {
                if (!refused.Contains(mod.FileName, StringComparer.OrdinalIgnoreCase)) refused.Add(mod.FileName);
                continue;
            }

            var wanted = ShouldBeOn(state, mod.FileName);
            if (mod.Enabled == wanted) continue;

            try
            {
                ModScanner.SetEnabled(mod, wanted);
                changed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Something has it open. Carrying on matters more than this one: stopping here
                // would leave every jar after it in whatever state the last step left it, and
                // those are the ones somebody would never get back.
                refused.Add(mod.FileName);
            }
        }

        return new BisectChange(changed, refused);
    }

    /// <summary>
    /// Puts every mod back the way it was before the hunt started. The one operation that has to
    /// work whatever else has gone wrong, so it reads only the list taken at the beginning.
    /// </summary>
    public static BisectChange Restore(BisectState state, IReadOnlyList<ModEntry> mods) =>
        Apply(state with { Off = [] }, mods);

    /// <summary>The first half, which is the half that gets switched off.</summary>
    private static List<string> Half(List<string> suspects) => [.. suspects.Take(suspects.Count / 2)];

    private static List<string> Clean(IEnumerable<string> mods) =>
    [
        .. mods.Where(mod => !string.IsNullOrWhiteSpace(mod))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(mod => mod, StringComparer.OrdinalIgnoreCase),
    ];
}
