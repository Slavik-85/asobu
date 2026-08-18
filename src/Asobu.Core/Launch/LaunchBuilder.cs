using System.Text.RegularExpressions;
using Asobu.Core.Accounts;
using Asobu.Core.Instances;
using Asobu.Core.Minecraft;

namespace Asobu.Core.Launch;

public sealed record LaunchPlan(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>
    /// The command line with the access token replaced. Use this anywhere it could be written
    /// to a log or shown on screen — the real token is a live credential.
    /// </summary>
    public string SafeCommandLine(string? accessToken)
    {
        var rendered = string.Join(' ', [Executable, .. Arguments]);
        return accessToken is { Length: > 4 } ? rendered.Replace(accessToken, "<redacted>") : rendered;
    }
}

/// <summary>
/// Assembles the java command line. This is where launchers usually go wrong, so it handles
/// both argument grammars, legacy asset layouts and pre-1.13 versions that ship no JVM args.
/// </summary>
public sealed partial class LaunchBuilder(AsobuPaths paths, MinecraftInstaller installer)
{
    private const string LauncherName = "asobu";
    private const string LauncherVersion = "0.1";

    public LaunchPlan Build(
        VersionJson version,
        Instance instance,
        LauncherSettings settings,
        MinecraftSession session,
        string javaExecutable)
    {
        var platform = RuleContext.Current;
        var gameDirectory = paths.InstanceGameDir(instance.Id);
        Directory.CreateDirectory(gameDirectory);

        var classpath = string.Join(Path.PathSeparator, installer.BuildClasspath(version, platform));
        var assetsRoot = UsesLegacyAssets(version) ? installer.VirtualAssetsDir(version) : paths.Assets;

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["natives_directory"] = paths.NativesDir(version.Id),
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = LauncherVersion,
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = paths.Libraries,
            ["version_name"] = version.Id,
            ["version_type"] = version.Type ?? "release",
            ["game_directory"] = gameDirectory,
            ["assets_root"] = assetsRoot,
            ["game_assets"] = assetsRoot,
            ["assets_index_name"] = version.AssetIndex?.Id ?? version.Assets ?? "legacy",
            ["auth_player_name"] = session.Username,
            ["auth_uuid"] = session.Uuid,
            ["auth_access_token"] = session.AccessToken,
            ["auth_session"] = $"token:{session.AccessToken}:{session.Uuid}",
            ["auth_xuid"] = session.Xuid ?? "",
            ["clientid"] = "",
            ["user_type"] = session.UserType,
            ["user_properties"] = "{}",
            ["resolution_width"] = "854",
            ["resolution_height"] = "480",
        };

        var arguments = new List<string>
        {
            $"-Xms{settings.MinMemoryMb}M",
            $"-Xmx{settings.MaxMemoryMb}M",
        };

        if (version.Logging?.Client is { } logging)
        {
            var configFile = Path.Combine(paths.LogConfigs, logging.File.Id ?? "log4j2.xml");
            arguments.Add(logging.Argument.Replace("${path}", configFile, StringComparison.Ordinal));
        }

        if (version.Arguments is { } structured)
        {
            arguments.AddRange(structured.Jvm
                .Where(a => RuleEvaluator.Allows(a, platform))
                .SelectMany(a => a.Values)
                .Select(value => Substitute(value, values)));
        }
        else
        {
            // 1.12.2 and older ship no jvm argument list; the launcher supplies these itself.
            arguments.Add($"-Djava.library.path={paths.NativesDir(version.Id)}");
            arguments.Add("-cp");
            arguments.Add(classpath);
        }

        if (settings.ExtraJvmArguments is { Length: > 0 } extra)
            arguments.AddRange(Tokenize(extra));

        arguments.Add(version.MainClass
            ?? throw new InvalidOperationException($"Version '{version.Id}' declares no main class."));

        if (version.Arguments is { } gameArgs)
        {
            arguments.AddRange(gameArgs.Game
                .Where(a => RuleEvaluator.Allows(a, platform))
                .SelectMany(a => a.Values)
                .Select(value => Substitute(value, values)));
        }
        else if (version.MinecraftArguments is { Length: > 0 } legacy)
        {
            arguments.AddRange(legacy
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => Substitute(value, values)));
        }

        return new LaunchPlan(javaExecutable, arguments, gameDirectory);
    }

    /// <summary>Pre-1.7.3 wants a real folder tree of assets rather than the hashed object store.</summary>
    private static bool UsesLegacyAssets(VersionJson version) =>
        version.AssetIndex?.Id is "legacy" or "pre-1.6";

    private static string Substitute(string template, IReadOnlyDictionary<string, string> values)
    {
        if (!template.Contains("${", StringComparison.Ordinal)) return template;

        return PlaceholderPattern().Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
    }

    /// <summary>Splits user-supplied JVM arguments, honouring double quotes around paths.</summary>
    private static IEnumerable<string> Tokenize(string arguments) =>
        TokenPattern().Matches(arguments).Select(m => m.Value.Trim('"'));

    [GeneratedRegex(@"\$\{([A-Za-z0-9_]+)\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"[^\s""]+|""[^""]*""")]
    private static partial Regex TokenPattern();
}
