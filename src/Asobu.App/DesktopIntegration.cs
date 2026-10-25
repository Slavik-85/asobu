using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Asobu.Core;
using Avalonia.Platform;

namespace Asobu.App;

/// <summary>
/// Puts the AppImage in the Linux app menu, the way an installed app is.
///
/// An AppImage is one file somebody downloaded, and a desktop never hears about it: it runs from
/// wherever it was saved, with no entry in the menu and no icon on the dock. So on its first run
/// it writes the desktop entry and the icon an installer would have, into the user's own share
/// folder. A .deb or .rpm brings its own system-wide entry, and the AppImage's is then removed so
/// the menu does not keep launching the old file.
/// </summary>
public static class DesktopIntegration
{
    private const string Id = "cc.asobu.Asobu";

    /// <summary>Marks an entry this wrote, so it is never mistaken for somebody's own.</summary>
    private const string Marker = "X-Asobu-Integrated=AppImage";

    public static void Run(AsobuLauncher launcher)
    {
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            var share = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
                ? data
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

            var entry = Path.Combine(share, "applications", Id + ".desktop");
            var icon = Path.Combine(share, "icons", "hicolor", "256x256", "apps", Id + ".png");

            var ours = File.Exists(entry) && File.ReadLines(entry).Any(line => line.Trim() == Marker);

            // Installed from a package, which owns the real entry and ours would shadow. Not a
            // tarball: that brings no entry of its own, and removing ours would leave none at all.
            if (AsobuPaths.PackageType() is "deb" or "rpm" or "arch")
            {
                if (ours)
                {
                    File.Delete(entry);
                    if (File.Exists(icon)) File.Delete(icon);
                }

                return;
            }

            if (Environment.GetEnvironmentVariable("APPIMAGE") is not { Length: > 0 } appImage) return;

            // This process's own AppImage, not one it was started from: APPIMAGE is inherited, so
            // a build run from inside some other AppImage — a terminal, an editor — would put that
            // in the menu as Asobu. And a plain path: a newline in it would add lines of its own
            // to the entry.
            if (!Path.IsPathRooted(appImage) || appImage.Any(char.IsControl)) return;
            if (Environment.GetEnvironmentVariable("APPDIR") is not { Length: > 0 } appDir
                || !AppContext.BaseDirectory.StartsWith(appDir, StringComparison.Ordinal))
                return;

            // A package's entry is already in the menu. Ours, left from before it was installed,
            // goes: a user entry outranks the system one, so the menu kept opening this AppImage
            // and the installed package could only be reached from a terminal.
            if (File.Exists($"/usr/share/applications/{Id}.desktop"))
            {
                if (ours)
                {
                    File.Delete(entry);
                    if (File.Exists(icon)) File.Delete(icon);
                }

                return;
            }

            // The user removed ours on purpose.
            if (launcher.Settings.LinuxMenuEntryWritten && !File.Exists(entry)) return;

            var text = Entry(appImage);

            // Somebody's own entry under our name, or ours already pointing at this file.
            if (File.Exists(entry) && (!ours || File.ReadAllText(entry) == text)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllText(entry, text);

            Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
            using (var source = AssetLoader.Open(new Uri("avares://Asobu.App/Assets/asobu.png")))
            using (var target = File.Create(icon))
                source.CopyTo(target);

            launcher.Settings.LinuxMenuEntryWritten = true;
            launcher.SaveSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A menu entry is a nicety. Not having one is no reason to fail to start.
        }
    }

    private static string Entry(string appImage) => $"""
        [Desktop Entry]
        Type=Application
        Name=Asobu
        GenericName=Minecraft Launcher
        Comment=Play Minecraft: Java Edition with mods, instances and friends
        Exec={ExecArgument(appImage)}
        TryExec={appImage.Replace("\\", "\\\\")}
        Icon={Id}
        Terminal=false
        Categories=Game;
        Keywords=minecraft;launcher;mods;modrinth;curseforge;
        StartupWMClass=asobu
        {Marker}

        """;

    /// <summary>
    /// A path as the desktop-entry spec wants it in Exec: % doubled, then quoted with " ` $ \
    /// escaped inside the quotes, then every backslash doubled again for the file's own string
    /// escaping. An AppImage saved under "My Games" would otherwise launch "My".
    /// </summary>
    private static string ExecArgument(string path)
    {
        var quoted = "\"" + Regex.Replace(path.Replace("%", "%%"), @"([""`$\\])", @"\$1") + "\"";
        return quoted.Replace("\\", "\\\\");
    }
}
