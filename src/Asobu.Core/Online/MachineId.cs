using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Asobu.Core.Online;

/// <summary>
/// A stable, one-way name for this computer, used only to cap how many offline accounts one
/// machine can put on the friends network.
///
/// What leaves here is a SHA-256 digest and never the thing it was made from. That matters: the
/// underlying values are real machine identifiers — Windows keeps one under Cryptography and
/// systemd keeps another in /etc/machine-id — and both are used by other software to recognise a
/// particular computer. Sending either as-is would hand the server a durable fingerprint it has
/// no use for. A digest answers the only question being asked, which is whether two requests came
/// from the same place, and answers nothing else. The server then keys it again through a salt
/// of its own before writing anything down, so what lands in its state file cannot be matched
/// back to this even by someone holding both.
///
/// It is worth being plain about what this is not. It is a speed bump, not a wall: anyone
/// determined enough to change the value can, and the same person on a second computer counts as
/// a second computer, because they are. The ceiling exists to stop a script making a thousand
/// accounts in an afternoon, and for that it is enough.
/// </summary>
public static class MachineId
{
    /// <summary>The digest to send. Same machine, same answer; different machine, different answer.</summary>
    public static string ForNetwork(AsobuPaths paths)
    {
        var raw = Encoding.UTF8.GetBytes("asobu-machine " + Underlying(paths));
        return Convert.ToHexStringLower(SHA256.HashData(raw));
    }

    /// <summary>
    /// The most durable identifier this platform will give up, or one of our own making.
    ///
    /// In that order deliberately: a value the operating system already keeps survives Asobu
    /// being uninstalled and reinstalled, which is the case the ceiling most needs to hold for.
    /// </summary>
    private static string Underlying(AsobuPaths paths)
    {
        if (OperatingSystem.IsWindows() && WindowsMachineGuid() is { Length: > 0 } guid) return guid;

        // systemd's, and the older D-Bus location for systems without it. Both are a hex string
        // written once when the system was installed.
        foreach (var path in (string[])["/etc/machine-id", "/var/lib/dbus/machine-id"])
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path).Trim();
                if (text.Length > 0) return text;
            }
            catch (Exception)
            {
                // Unreadable is the same as absent here; the next one, or the fallback, answers.
            }
        }

        return OurOwn(paths);
    }

    /// <summary>
    /// A machine id of our own, made once and kept beside the settings.
    ///
    /// Weaker than the platform's, since reinstalling Asobu makes a new one — but this is only
    /// reached where the platform offers nothing, and a ceiling that resets on reinstall still
    /// beats no ceiling. A failure to write is not worth failing over: an id that lasts as long
    /// as this launch is still an id.
    /// </summary>
    private static string OurOwn(AsobuPaths paths)
    {
        var file = Path.Combine(paths.Root, "machine-id");

        try
        {
            if (File.Exists(file))
            {
                var existing = File.ReadAllText(file).Trim();
                if (existing.Length > 0) return existing;
            }

            var made = Guid.NewGuid().ToString("n");
            Directory.CreateDirectory(paths.Root);
            File.WriteAllText(file, made);
            return made;
        }
        catch (Exception)
        {
            return Guid.NewGuid().ToString("n");
        }
    }

    /// <summary>
    /// HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid, written when Windows was installed.
    ///
    /// Read through advapi32 rather than through Microsoft.Win32.Registry, which is a package
    /// this project does not carry — the same trade the title bar makes for its three DWM calls.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? WindowsMachineGuid()
    {
        const string subKey = @"SOFTWARE\Microsoft\Cryptography";
        const string name = "MachineGuid";

        const int restrictToString = 0x00000002;   // RRF_RT_REG_SZ

        // Asobu may be running as a 32-bit process on a 64-bit Windows, where the plain view of
        // this key is a different, redirected one that does not hold the value.
        const int sixtyFourBitView = 0x00010000;   // RRF_SUBKEY_WOW6464KEY

        var hive = new IntPtr(unchecked((int)0x80000002)); // HKEY_LOCAL_MACHINE
        var flags = restrictToString | sixtyFourBitView;

        try
        {
            // Asked with no buffer first, which answers with the size one would need.
            var size = 0;
            if (RegGetValueW(hive, subKey, name, flags, out _, null, ref size) != 0 || size <= 0) return null;

            var buffer = new byte[size];
            if (RegGetValueW(hive, subKey, name, flags, out _, buffer, ref size) != 0) return null;

            // Wide characters, and size counts bytes including the terminator this trims off.
            var text = Encoding.Unicode.GetString(buffer, 0, Math.Min(size, buffer.Length)).TrimEnd('\0');
            return text.Length > 0 ? text : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegGetValueW")]
    private static extern int RegGetValueW(
        IntPtr hive, string subKey, string name, int flags, out int kind, byte[]? data, ref int size);
}
