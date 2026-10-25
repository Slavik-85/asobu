namespace Asobu.Core.Mods;

/// <summary>
/// What an older build's find-the-mod-by-halving hunt had switched off and not yet put back.
///
/// Hunting by halving is gone: it took launch after launch with half a modpack missing, which is
/// not a fix. Somebody who updated in the middle of one would otherwise be left with half their
/// mods off and nothing that remembers which half, so the record is read once, those mods go
/// back on, and it is dropped.
/// </summary>
public sealed class LeftoverHunt
{
    /// <summary>Every mod that was on when the hunt started — the only part worth reading now.</summary>
    public List<string> Restore { get; set; } = [];

    /// <summary>
    /// Switches the hunt's mods back on. False when one would not move, so the record is kept and
    /// this is tried again next start.
    /// </summary>
    public bool PutBack(string modsDirectory)
    {
        var mods = ModScanner.Scan(modsDirectory);
        var complete = true;

        foreach (var mod in mods)
        {
            if (mod.Enabled || !Restore.Contains(mod.FileName, StringComparer.OrdinalIgnoreCase)) continue;

            // A jar and its own .disabled twin side by side: switching this one on would land on
            // top of the other, so both are left as they are.
            if (mods.Any(other => other.Enabled && other.FileName.Equals(mod.FileName, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                ModScanner.SetEnabled(mod, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                complete = false;
            }
        }

        return complete;
    }
}
