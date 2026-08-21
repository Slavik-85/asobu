using Asobu.Core.Diagnostics;

namespace Asobu.Core.Tests;

/// <summary>
/// What the launcher reads out of a launch log, and — just as much — what it then says about it.
///
/// The wording is asserted alongside the parse on purpose. Every one of these findings exists to
/// be shown to somebody, and a row that parsed perfectly and then renders as "wants a different
/// version" has failed at the only thing it was for.
/// </summary>
public class DiagnosticsTests
{
    // Fabric's own words, from a real refusal to start: the suggested fix, then the detail
    // underneath explaining which mod objected and to what.
    private const string FabricIncompatible = """
        Incompatible mod set!
        net.fabricmc.loader.impl.FormattedException: Some of your mods are incompatible with the game or each other!

        A potential solution has been determined, this may resolve your problem:
            - Replace mod 'Sodium' (sodium) 0.9.2-alpha.4 with any 0.9.x version that is compatible with:
                - iris 1.11.2

        More details:
            - Mod 'Iris' (iris) 1.11.2 is incompatible with version 0.9.2-alpha.4 or earlier of mod 'Sodium' (sodium), yet a conflicting version is present: 0.9.2-alpha.4!
        """;

    // A breakage the loader had no suggestion for, which is the shape that has to be described
    // from the incompatibility line alone.
    private const string FabricBreakageOnly = """
        Incompatible mod set!
            - Mod 'Iris' (iris) 1.11.2 is incompatible with version 0.6.0 or earlier of mod 'Indium' (indium), yet a conflicting version is present: 0.6.0!
        """;

    private const string FabricWrongVersion = """
        Mod 'Create' (create) 6.0.4 requires version 0.16.0 or later of fabric-api, but only the wrong version is present: fabric-api 0.15.11!
        """;

    private const string FabricMissing = """
        Mod 'Create' (create) 6.0.4 requires any version of mod 'Fabric API' (fabric-api), which is missing!
        """;

    private const string ForgeTable = """
        Missing or unsupported mandatory dependencies:
            Mod ID: 'jei', Requested by: 'ars_nouveau', Expected range: '[15.2.0,)', Actual version: '15.0.0'
            Mod ID: 'curios', Requested by: 'ars_nouveau', Expected range: '[5.1.0,)', Actual version: '[MISSING]'
        """;

    [Fact]
    public void Conflict_DescribesAnExclusiveFloorRatherThanShrugging()
    {
        var conflict = Assert.Single(ModConflicts.Find(FabricBreakageOnly));

        // The bound the loader stated is "past 0.6.0". Saying so is the whole value of the row:
        // "a different version" is what it looked like before Above was a case in WantedLabel,
        // and it is indistinguishable from every other row on the screen.
        Assert.Equal("later than 0.6.0", conflict.WantedLabel);
        Assert.DoesNotContain("a different version", conflict.Detail);
    }

    [Fact]
    public void Conflict_ReadsFabricsOwnSuggestedFix()
    {
        var conflict = ModConflicts.Find(FabricIncompatible)
            .Single(c => c.ModId.Equals("sodium", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("0.9.2-alpha.4", conflict.Present);
        Assert.Equal("0.9", conflict.Wanted.AtLeast);
        Assert.Equal("0.10", conflict.Wanted.Below);
        Assert.Equal("0.9 up to 0.10", conflict.WantedLabel);
    }

    [Fact]
    public void Conflict_CarriesTheOtherEndOfTheDisagreement()
    {
        var conflict = ModConflicts.Find(FabricIncompatible)
            .Single(c => c.ModId.Equals("sodium", StringComparison.OrdinalIgnoreCase));

        // Fabric only ever proposes moving one of the two. When no build of Sodium fits, moving
        // Iris instead is the remaining answer — so the row has to know Iris is the other end.
        Assert.NotNull(conflict.Alternative);
        Assert.Equal("iris", conflict.Alternative!.ModId);
    }

    [Fact]
    public void Conflict_DoesNotOfferTheSameDisagreementTwice()
    {
        // One problem, one row. The suggested fix and the incompatibility line underneath it
        // describe the same pair of mods, and offering both invites fixing it from both ends.
        Assert.Single(ModConflicts.Find(FabricIncompatible));
    }

    [Fact]
    public void Conflict_ReadsAWrongVersionThatIsPresent()
    {
        var conflict = Assert.Single(ModConflicts.Find(FabricWrongVersion));

        Assert.Equal("fabric-api", conflict.ModId);
        Assert.Equal("0.15.11", conflict.Present);
        Assert.Equal("0.16.0 or later", conflict.WantedLabel);
    }

    [Fact]
    public void Conflict_LeavesForgesMissingRowToTheDependencyReader()
    {
        var found = ModConflicts.Find(ForgeTable);

        // '[MISSING]' means absent, not out of date. A swap for it could only ever fail.
        Assert.Equal(["jei"], found.Select(c => c.ModId));
    }

    [Fact]
    public void Dependency_ReadsWhatFabricSaysIsMissing()
    {
        var missing = Assert.Single(MissingDependencies.Find(FabricMissing));

        Assert.Equal("fabric-api", missing.Id);
        Assert.Equal("Fabric API", missing.Name);
        Assert.Equal("Create", missing.RequiredBy);
    }

    [Fact]
    public void Dependency_ReadsForgesMissingRow()
    {
        var missing = Assert.Single(MissingDependencies.Find(ForgeTable));

        Assert.Equal("curios", missing.Id);
        Assert.Equal("ars_nouveau", missing.RequiredBy);
    }

    [Theory]
    // A game version in front of the mod's own is not part of it.
    [InlineData("mc26.2-0.9.1-fabric", "0.9.0", true)]
    // Build metadata is not four more components.
    [InlineData("1.11.2+26.2", "1.11.2", true)]
    [InlineData("0.9", "0.10", false)]
    public void Bound_ComparesTheModsVersionAndNotTheGames(string version, string floor, bool accepted)
    {
        Assert.Equal(accepted, new VersionBound(floor, null, null).Accepts(version));
    }

    [Fact]
    public void Bound_TreatsAFamilyAsAClosedRange()
    {
        var bound = ModConflicts.Bound("0.9.x");

        Assert.True(bound.Accepts("0.9.4"));
        Assert.False(bound.Accepts("0.10.0"));
    }

    [Fact]
    public void Bound_ReadsAnExclusiveFloorAsStrictlyAbove()
    {
        var bound = new VersionBound(null, null, null) { Above = "0.6.0" };

        Assert.False(bound.Accepts("0.6.0"));
        Assert.True(bound.Accepts("0.6.1"));
    }
}
