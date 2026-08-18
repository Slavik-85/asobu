using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Asobu.Core.Minecraft;

/// <summary>A rule guarding a library, an argument or a native payload.</summary>
public sealed class Rule
{
    /// <summary>allow | disallow</summary>
    public required string Action { get; init; }

    public OsRule? Os { get; init; }

    /// <summary>Launch-time toggles, e.g. "is_demo_user", "has_custom_resolution", "has_quick_plays_support".</summary>
    public Dictionary<string, bool>? Features { get; init; }
}

public sealed class OsRule
{
    /// <summary>windows | osx | linux</summary>
    public string? Name { get; init; }

    /// <summary>Regex matched against the OS version, e.g. "^10\\.".</summary>
    public string? Version { get; init; }

    /// <summary>x86 | x64 | arm64 | ...</summary>
    public string? Arch { get; init; }
}

/// <summary>The platform and feature set that rules are evaluated against.</summary>
public sealed class RuleContext
{
    public required string OsName { get; init; }
    public required string OsVersion { get; init; }
    public required string OsArch { get; init; }
    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>();

    /// <summary>This machine, with no launch features enabled.</summary>
    public static RuleContext Current { get; } = Detect();

    public RuleContext WithFeatures(params string[] enabled)
    {
        var features = new Dictionary<string, bool>(Features, StringComparer.Ordinal);
        foreach (var name in enabled) features[name] = true;
        return new RuleContext { OsName = OsName, OsVersion = OsVersion, OsArch = OsArch, Features = features };
    }

    private static RuleContext Detect() => new()
    {
        OsName = OperatingSystem.IsWindows() ? "windows"
               : OperatingSystem.IsMacOS() ? "osx"
               : OperatingSystem.IsLinux() ? "linux"
               : "unknown",

        // Mojang version regexes are written against Java's os.version. On Windows that is
        // "<major>.<minor>" — "10.0" for both Windows 10 and 11 — which is what rules like "^10\." expect.
        // ponytail: on macOS this reports the Darwin kernel version, not the product version. Only
        // pre-1.13 osx LWJGL rules care, so fix it (sw_vers / SystemVersion.plist) when macOS lands.
        OsVersion = $"{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}",

        OsArch = NormalizeArch(RuntimeInformation.OSArchitecture.ToString()),
    };

    internal static string NormalizeArch(string arch) => arch.ToLowerInvariant() switch
    {
        "x64" or "x86_64" or "amd64" => "x64",
        "x86" or "i386" or "i686" => "x86",
        "arm64" or "aarch64" => "arm64",
        "arm" or "arm32" => "arm32",
        var other => other,
    };
}

public static class RuleEvaluator
{
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Mojang semantics: no rules means allow. Otherwise start from disallow and let every
    /// matching rule overwrite the verdict in document order — the last match wins.
    /// </summary>
    public static bool Allows(IReadOnlyList<Rule>? rules, RuleContext context)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
            if (Matches(rule, context))
                allowed = rule.Action == "allow";

        return allowed;
    }

    public static bool Allows(Library library, RuleContext context) => Allows(library.Rules, context);

    public static bool Allows(ConditionalArgument argument, RuleContext context) => Allows(argument.Rules, context);

    private static bool Matches(Rule rule, RuleContext context)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && !os.Name.Equals(context.OsName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (os.Arch is not null && RuleContext.NormalizeArch(os.Arch) != context.OsArch)
                return false;

            if (os.Version is not null && !SafeMatch(context.OsVersion, os.Version))
                return false;
        }

        if (rule.Features is { } features)
        {
            foreach (var (name, expected) in features)
            {
                context.Features.TryGetValue(name, out var actual);
                if (actual != expected) return false;
            }
        }

        return true;
    }

    private static bool SafeMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, RegexBudget);
        }
        catch (Exception e) when (e is RegexMatchTimeoutException or ArgumentException)
        {
            // A malformed or pathological pattern must not take the launcher down; treat it as no match.
            return false;
        }
    }
}
