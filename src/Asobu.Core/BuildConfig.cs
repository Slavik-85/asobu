using System.Reflection;

namespace Asobu.Core;

/// <summary>
/// Values baked in when this build was compiled, rather than configured afterwards.
///
/// This exists for the CurseForge API key, which their catalogue will not answer without.
/// CurseForge issue a key to each application, and the same approach every launcher takes is to
/// compile their own key into their own official builds — Prism does exactly this, which is why
/// building Prism from source without supplying a key leaves CurseForge switched off.
///
/// Supply one at build time with either of:
///
///     dotnet build -p:CurseForgeApiKey=YOUR_KEY
///     set ASOBU_CURSEFORGE_API_KEY=YOUR_KEY  (then build normally)
///
/// A key pasted into Settings still wins over this, so a build without one is fully usable.
/// </summary>
public static class BuildConfig
{
    public static string? CurseForgeApiKey { get; } = Read(nameof(CurseForgeApiKey));

    public static bool HasCurseForgeKey => CurseForgeApiKey is { Length: > 0 };

    /// <summary>
    /// Read back off an assembly attribute the build wrote. Reflection rather than a generated
    /// constant so the project file stays the only place that knows about this.
    /// </summary>
    private static string? Read(string key)
    {
        var value = typeof(BuildConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)
            ?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
