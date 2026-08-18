using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Asobu.Core.Launch;

/// <summary>
/// Which GPU Windows hands the game. This is the same per-executable setting as
/// Settings &gt; Display &gt; Graphics, written for the java binary Asobu launches — which is what
/// actually matters on laptops, where Java otherwise lands on the integrated chip.
/// </summary>
public static class GpuPreferences
{
    private const string PreferenceKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string DisplayAdaptersKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static void Apply(string executablePath, GpuPreference preference)
    {
        if (!OperatingSystem.IsWindows()) return;
        ApplyWindows(executablePath, preference);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(string executablePath, GpuPreference preference)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PreferenceKey, writable: true);
            if (key is null) return;

            if (preference == GpuPreference.Auto)
            {
                key.DeleteValue(executablePath, throwOnMissingValue: false);
                return;
            }

            // 1 = power saving, 2 = high performance. Anything else means "let Windows decide".
            var value = preference == GpuPreference.HighPerformance ? 2 : 1;
            key.SetValue(executablePath, $"GpuPreference={value};", RegistryValueKind.String);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A locked-down machine just gets Windows' default choice; not worth failing a launch over.
        }
    }

    /// <summary>Display adapter names, so Settings can show what the choice is actually picking between.</summary>
    public static IReadOnlyList<string> Detect()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return DetectWindows();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> DetectWindows()
    {
        var adapters = new List<string>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayAdaptersKey);
            if (root is null) return adapters;

            foreach (var name in root.GetSubKeyNames())
            {
                if (!name.All(char.IsDigit)) continue;

                using var adapter = root.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is string description && !adapters.Contains(description))
                    adapters.Add(description);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return adapters;
    }
}
