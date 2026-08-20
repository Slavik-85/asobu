using System;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Mods;

namespace Asobu.App.ViewModels;

/// <summary>
/// Putting a mod into an instance, and saying what happened on the card that asked. Shared
/// because two pages offer the same button and the wording of a refusal is the part worth
/// getting right once.
/// </summary>
public static class ModInstall
{
    /// <summary>
    /// Null when the mod went in. Otherwise why it did not — returned as well as written on the
    /// card, so a sheet that asked for the instance can say so and stay open for another go.
    /// </summary>
    public static async Task<string?> RunAsync(AsobuLauncher launcher, Instance? instance, ModCard? card)
    {
        if (card is null || instance is null || card.IsInstalling) return null;

        card.IsInstalling = true;
        card.Notice = null;

        try
        {
            var result = await launcher.InstallModAsync(instance, card.Mod);

            if (result.Installed)
            {
                card.IsInstalled = true;

                // Worth naming only when there was a choice to make: for a mod both shops carry,
                // which one actually handed the file over is the interesting part.
                var added = card.Mod is { Modrinth: not null, CurseForge: not null }
                    ? $"Added {result.FileName} from {result.From}"
                    : $"Added {result.FileName}";

                // Said, not slipped in: files appearing in a mods folder that nobody asked for
                // is the sort of thing worth being told about, however helpful it is.
                card.Notice = result.Dependencies.Count switch
                {
                    0 => added,
                    1 => $"{added}, with 1 dependency",
                    var n => $"{added}, with {n} dependencies",
                };

                return null;
            }

            // Three different reasons land here, and the difference matters to the user. Kept
            // short: a wrapped paragraph would stretch every tile in the same row of a grid.
            // Blocked only survives to here when the other provider had nothing either.
            card.Notice = result.Reason
                ?? (result.Blocked
                    ? "The author allows downloads from their page only."
                    // A loader means nothing to a resource pack, so a refusal must not cite one.
                    : card.Mod.Kind is ModKind.Mod or ModKind.Any
                        ? $"No build for {instance.LoaderName} {instance.MinecraftVersion}."
                        : $"No build for Minecraft {instance.MinecraftVersion}.");
        }
        catch (Exception ex)
        {
            card.Notice = ex.Message;
        }
        finally
        {
            card.IsInstalling = false;
        }

        return card.Notice;
    }
}
