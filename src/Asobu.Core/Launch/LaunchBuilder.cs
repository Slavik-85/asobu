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
        string javaExecutable,
        string? joinServer = null,
        SessionUpstreams? sessionHosts = null)
    {
        var platform = RuleContext.Current;
        var gameDirectory = paths.InstanceGameDir(instance.Folder);
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

        // Where the game asks whether a player is who they say they are. Pointed at Asobu's own
        // stand-in so a friend without a Microsoft account can be let into a world — see
        // SessionShim, which forwards everything it does not answer itself.
        //
        // All four, because authlib takes them as a set and refuses a partial one out loud:
        //
        //     Ignoring hosts properties. All need to be set:
        //     [minecraft.api.auth.host, minecraft.api.account.host, minecraft.api.session.host]
        //
        // Which list it names varies by version, so the safe move is to give it every one. That
        // refusal is also the graceful failure on a version predating these properties: the game
        // carries on exactly as it did before.
        if (sessionHosts is { } shim)
        {
            arguments.Add($"-Dminecraft.api.auth.host={shim.Auth}");
            arguments.Add($"-Dminecraft.api.account.host={shim.Account}");
            arguments.Add($"-Dminecraft.api.session.host={shim.Session}");
            arguments.Add($"-Dminecraft.api.services.host={shim.Services}");
        }

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
                .Select(value => Substitute(value, JvmValues(values, version))));
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

        if (joinServer is { Length: > 0 } address) arguments.AddRange(JoinArguments(version, address));

        return new LaunchPlan(javaExecutable, arguments, gameDirectory);
    }

    /// <summary>
    /// The same substitutions, except that <c>version_name</c> names the client jar rather than the
    /// profile.
    ///
    /// Only Forge reads it from a JVM argument, and what it does with it is this:
    ///
    /// <code>-DignoreList=…,client-extra,fmlcore,…,forge-,${version_name}.jar</code>
    ///
    /// BootstrapLauncher matches that list against the file names on the class path and keeps
    /// whatever matches <i>off</i> the module path. The entry is there to exclude the vanilla
    /// client jar, which Forge loads itself through its own union filesystem. Substituting the
    /// profile — "1.19.2-forge-43.5.0" — produces a name no file has, so nothing is excluded, the
    /// vanilla jar lands on the module path beside Forge's own view of it, and two modules end up
    /// exporting com.mojang.blaze3d.system. Java refuses to start:
    ///
    /// <code>Module minecraft contains package com.mojang.blaze3d.system</code>
    ///
    /// Taken from JarVersionId rather than from the instance, because that is the same property
    /// the class path is built from — so the name here cannot drift from the file actually there.
    ///
    /// Game arguments keep the profile: <c>--version</c> is what F3 shows, and it should say which
    /// profile is running rather than which jar it was built on.
    /// </summary>
    private static Dictionary<string, string> JvmValues(Dictionary<string, string> values, VersionJson version)
    {
        if (version.JarVersionId == version.Id) return values;

        return new Dictionary<string, string>(values, StringComparer.Ordinal)
        {
            ["version_name"] = version.JarVersionId,
        };
    }

    /// <summary>
    /// How to tell this version of the game to connect to a server on the way in.
    ///
    /// Two spellings, and which one is right is asked of the version rather than worked out from
    /// its number. 1.20 brought --quickPlayMultiplayer and every version declares its own game
    /// arguments, so a version that knows the flag says so in its own document — which is a
    /// better answer than a comparison against a version number that stops being true the year
    /// somebody backports it.
    ///
    /// Older versions take --server and --port instead, and want them apart.
    /// </summary>
    private static IEnumerable<string> JoinArguments(VersionJson version, string address)
    {
        var understandsQuickPlay = version.Arguments?.Game
            .SelectMany(a => a.Values)
            .Any(value => value.Contains("--quickPlayMultiplayer", StringComparison.Ordinal)) ?? false;

        if (understandsQuickPlay)
        {
            yield return "--quickPlayMultiplayer";
            yield return address;
            yield break;
        }

        var (host, port) = SplitAddress(address);

        yield return "--server";
        yield return host;
        yield return "--port";
        yield return port;
    }

    /// <summary>
    /// "play.example.com:25566" as a host and a port, defaulting to Minecraft's own.
    ///
    /// Only splits on the last colon, and only when what follows is a number: an IPv6 address is
    /// full of colons and none of them separates a port.
    /// </summary>
    private static (string Host, string Port) SplitAddress(string address)
    {
        var cut = address.LastIndexOf(':');

        if (cut > 0 && ushort.TryParse(address[(cut + 1)..], out _))
            return (address[..cut], address[(cut + 1)..]);

        return (address, "25565");
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
