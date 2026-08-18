using System.Text.Json;
using Asobu.Core.Minecraft;

namespace Asobu.Core.Tests;

internal static class Ctx
{
    public static RuleContext Windows { get; } = new() { OsName = "windows", OsVersion = "10.0", OsArch = "x64" };
    public static RuleContext MacOs { get; } = new() { OsName = "osx", OsVersion = "14.5", OsArch = "arm64" };
}

public class RuleEvaluatorTests
{
    private static Rule Parse(string json) => JsonSerializer.Deserialize<Rule>(json, MojangJson.Options)!;

    [Fact]
    public void NoRules_IsAllowed()
    {
        Assert.True(RuleEvaluator.Allows(rules: null, Ctx.Windows));
        Assert.True(RuleEvaluator.Allows(Array.Empty<Rule>(), Ctx.Windows));
    }

    [Fact]
    public void LastMatchingRuleWins()
    {
        // Mojang's real shape for "everyone except macOS".
        var rules = new[]
        {
            Parse("""{"action":"allow"}"""),
            Parse("""{"action":"disallow","os":{"name":"osx"}}"""),
        };

        Assert.True(RuleEvaluator.Allows(rules, Ctx.Windows));
        Assert.False(RuleEvaluator.Allows(rules, Ctx.MacOs));
    }

    [Fact]
    public void UnmatchedOsRule_LeavesDefaultDisallow()
    {
        var rules = new[] { Parse("""{"action":"allow","os":{"name":"linux"}}""") };
        Assert.False(RuleEvaluator.Allows(rules, Ctx.Windows));
    }

    [Fact]
    public void OsVersionRegex_MatchesWindows11AsTenDotZero()
    {
        // Windows 11 reports os.version "10.0" to Java, so "^10\." must match it.
        var rules = new[] { Parse("""{"action":"allow","os":{"name":"windows","version":"^10\\."}}""") };
        Assert.True(RuleEvaluator.Allows(rules, Ctx.Windows));
    }

    [Fact]
    public void MalformedRegex_DoesNotThrow()
    {
        var rules = new[] { Parse("""{"action":"allow","os":{"version":"([unclosed"}}""") };
        Assert.False(RuleEvaluator.Allows(rules, Ctx.Windows));
    }

    [Theory]
    [InlineData("x86_64", true)]
    [InlineData("amd64", true)]
    [InlineData("x64", true)]
    [InlineData("x86", false)]
    [InlineData("arm64", false)]
    public void ArchAliasesAreNormalized(string ruleArch, bool expected)
    {
        var rules = new[] { Parse($$$"""{"action":"allow","os":{"arch":"{{{ruleArch}}}"}}""") };
        Assert.Equal(expected, RuleEvaluator.Allows(rules, Ctx.Windows));
    }

    [Fact]
    public void FeatureRules_DefaultToDisabled()
    {
        var rules = new[] { Parse("""{"action":"allow","features":{"is_demo_user":true}}""") };

        Assert.False(RuleEvaluator.Allows(rules, Ctx.Windows));
        Assert.True(RuleEvaluator.Allows(rules, Ctx.Windows.WithFeatures("is_demo_user")));
    }
}

public class VersionJsonTests
{
    [Fact]
    public void Arguments_ParseMixedStringAndObjectForms()
    {
        const string json = """
        {
          "id": "test",
          "arguments": {
            "game": [
              "--username", "${auth_player_name}",
              { "rules": [{ "action": "allow", "features": { "is_demo_user": true } }], "value": "--demo" },
              { "rules": [{ "action": "allow", "features": { "has_custom_resolution": true } }],
                "value": ["--width", "${resolution_width}", "--height", "${resolution_height}"] }
            ],
            "jvm": ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"]
          }
        }
        """;

        var version = JsonSerializer.Deserialize<VersionJson>(json, MojangJson.Options)!;
        var arguments = version.Arguments!;

        Assert.Equal(4, arguments.Game.Count);
        Assert.Equal(new[] { "--username" }, arguments.Game[0].Values);
        Assert.Null(arguments.Game[0].Rules);
        Assert.Equal(new[] { "--demo" }, arguments.Game[2].Values);
        Assert.Equal(4, arguments.Game[3].Values.Count);
        Assert.Equal(3, arguments.Jvm.Count);

        var enabled = arguments.Game
            .Where(a => RuleEvaluator.Allows(a, Ctx.Windows))
            .SelectMany(a => a.Values);

        Assert.Equal(new[] { "--username", "${auth_player_name}" }, enabled);
    }

    [Fact]
    public void LegacyVersions_UseFlatArgumentString()
    {
        const string json = """
        { "id": "1.12.2", "minecraftArguments": "--username ${auth_player_name} --version ${version_name}" }
        """;

        var version = JsonSerializer.Deserialize<VersionJson>(json, MojangJson.Options)!;

        Assert.Null(version.Arguments);
        Assert.StartsWith("--username", version.MinecraftArguments);
    }
}

public class VersionResolverTests
{
    private static VersionJson Vanilla => new()
    {
        Id = "1.21.4",
        Type = "release",
        MainClass = "net.minecraft.client.main.Main",
        Assets = "26",
        JavaVersion = new JavaVersionRef { Component = "java-runtime-delta", MajorVersion = 21 },
        Libraries = [new Library { Name = "com.mojang:logging:1.2.7" }],
        Arguments = new Arguments { Game = [new ConditionalArgument { Values = ["--username"] }] },
    };

    private static VersionJson FabricChild => new()
    {
        Id = "fabric-loader-0.16.9-1.21.4",
        InheritsFrom = "1.21.4",
        MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
        Libraries = [new Library { Name = "net.fabricmc:fabric-loader:0.16.9" }],
        Arguments = new Arguments { Jvm = [new ConditionalArgument { Values = ["-DFabricMcEmu=..."] }] },
    };

    private static Task<VersionJson> Load(string id, CancellationToken _) =>
        Task.FromResult(id == "1.21.4" ? Vanilla : FabricChild);

    [Fact]
    public async Task VanillaPassesThroughUnchanged()
    {
        var resolved = await VersionResolver.ResolveAsync("1.21.4", Load);

        Assert.Equal("1.21.4", resolved.Id);
        Assert.Single(resolved.Libraries);
    }

    [Fact]
    public async Task ChildOverridesParentAndKeepsClasspathPriority()
    {
        var resolved = await VersionResolver.ResolveAsync("fabric-loader-0.16.9-1.21.4", Load);

        Assert.Equal("fabric-loader-0.16.9-1.21.4", resolved.Id);
        Assert.Null(resolved.InheritsFrom);

        // Child wins on scalars, inherits what it does not define.
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", resolved.MainClass);
        Assert.Equal("26", resolved.Assets);
        Assert.Equal(21, resolved.JavaVersion!.MajorVersion);

        // Loader libraries must come first on the classpath.
        Assert.Equal(
            new[] { "net.fabricmc:fabric-loader:0.16.9", "com.mojang:logging:1.2.7" },
            resolved.Libraries.Select(l => l.Name));

        // Argument lists concatenate rather than replace.
        Assert.Single(resolved.Arguments!.Game);
        Assert.Single(resolved.Arguments!.Jvm);
    }

    [Fact]
    public async Task CircularInheritanceIsRejected()
    {
        static Task<VersionJson> Loop(string id, CancellationToken _) =>
            Task.FromResult(new VersionJson { Id = id, InheritsFrom = id });

        await Assert.ThrowsAsync<InvalidOperationException>(() => VersionResolver.ResolveAsync("a", Loop));
    }
}
