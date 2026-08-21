using System.Reflection;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// Reading OptiFine's file names.
///
/// The only thing here that can be tested without the website is the naming, and it is also the
/// part most worth testing: everything else is one HTTP request and a regular expression over
/// somebody else's HTML, but which build counts as newest is arithmetic, and getting it wrong
/// installs the wrong jar silently.
/// </summary>
public class OptiFineTests
{
    private static OptiFineBuild? Describe(string fileName) =>
        (OptiFineBuild?)typeof(OptiFine)
            .GetMethod("Describe", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [fileName]);

    [Fact]
    public void Reads_a_file_name_apart()
    {
        var build = Describe("OptiFine_1.8.9_HD_U_M5.jar");

        Assert.NotNull(build);
        Assert.Equal("1.8.9", build!.MinecraftVersion);
        Assert.Equal("HD_U", build.Edition);
        Assert.False(build.Preview);
    }

    [Fact]
    public void Knows_a_preview_when_it_sees_one()
    {
        Assert.True(Describe("OptiFine_1.21.4_HD_U_J3_pre5.jar")!.Preview);
        Assert.False(Describe("OptiFine_1.21.4_HD_U_J3.jar")!.Preview);
    }

    /// <summary>
    /// The one that matters. Ordering these as plain text puts M10 before M5, so "newest" would
    /// be whichever build happened to have the smallest number after the letter — and somebody
    /// asking for the latest OptiFine would quietly get an old one.
    /// </summary>
    [Fact]
    public void Orders_a_two_digit_build_after_a_one_digit_one()
    {
        var five = Describe("OptiFine_1.8.9_HD_U_M5.jar")!.Release;
        var ten = Describe("OptiFine_1.8.9_HD_U_M10.jar")!.Release;

        Assert.True(string.CompareOrdinal(ten, five) > 0, $"M10 ({ten}) did not sort after M5 ({five})");
    }

    [Fact]
    public void Orders_a_later_letter_after_an_earlier_one()
    {
        var l9 = Describe("OptiFine_1.8.9_HD_U_L9.jar")!.Release;
        var m1 = Describe("OptiFine_1.8.9_HD_U_M1.jar")!.Release;

        Assert.True(string.CompareOrdinal(m1, l9) > 0);
    }

    [Theory]
    [InlineData("OptiFine_1.21.11_HD_U_J9.jar", "1.21.11")]
    [InlineData("OptiFine_1.12.2_HD_U_G5.jar", "1.12.2")]
    [InlineData("OptiFine_1.7.2_HD_U_E3.jar", "1.7.2")]
    public void Takes_the_minecraft_version_out_of_the_name(string file, string expected)
    {
        Assert.Equal(expected, Describe(file)!.MinecraftVersion);
    }

    /// <summary>Anything that is not one of theirs is not guessed at.</summary>
    [Theory]
    [InlineData("sodium-fabric-0.5.8.jar")]
    [InlineData("OptiFine.jar")]
    [InlineData("preview_OptiFine_1.21.4.jar")]
    public void Refuses_what_is_not_an_optifine_build(string file)
    {
        Assert.Null(Describe(file));
    }
}
