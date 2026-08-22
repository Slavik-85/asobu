using Asobu.Core.Instances;

namespace Asobu.Core.Tests;

/// <summary>
/// Deciding whether two instances would play the same, so pressing Join can just start.
///
/// The asymmetry is the point: a false mismatch costs somebody a rebuild of something they already
/// had, while a false match drops them into a world their client cannot play. Everything here
/// leans towards the first.
/// </summary>
public class InstanceFingerprintTests
{
    private static string Of(string version = "1.19.2", string loader = "forge",
                             string? loaderVersion = "43.5.0", params string[] mods) =>
        InstanceFingerprint.Of(version, loader, loaderVersion, mods);

    [Fact]
    public void The_same_setup_agrees_with_itself() =>
        Assert.Equal(Of(mods: ["jei.jar", "sodium.jar"]), Of(mods: ["jei.jar", "sodium.jar"]));

    /// <summary>The order a folder lists files in is not part of what an instance is.</summary>
    [Fact]
    public void The_order_mods_happen_to_be_listed_in_does_not_matter() =>
        Assert.Equal(Of(mods: ["sodium.jar", "jei.jar"]), Of(mods: ["jei.jar", "sodium.jar"]));

    [Fact]
    public void A_different_minecraft_version_is_a_different_instance() =>
        Assert.NotEqual(Of(version: "1.19.2", mods: ["jei.jar"]), Of(version: "1.20.1", mods: ["jei.jar"]));

    [Fact]
    public void A_different_loader_is_a_different_instance() =>
        Assert.NotEqual(Of(loader: "forge", mods: ["jei.jar"]), Of(loader: "fabric", mods: ["jei.jar"]));

    [Fact]
    public void A_different_loader_version_is_a_different_instance() =>
        Assert.NotEqual(Of(loaderVersion: "43.5.0", mods: ["jei.jar"]), Of(loaderVersion: "43.4.0", mods: ["jei.jar"]));

    [Fact]
    public void One_mod_more_is_a_different_instance() =>
        Assert.NotEqual(Of(mods: ["jei.jar"]), Of(mods: ["jei.jar", "sodium.jar"]));

    /// <summary>
    /// A version bump changes the file name, and it should count: the two builds are not
    /// interchangeable just because the mod has the same name.
    /// </summary>
    [Fact]
    public void A_newer_build_of_the_same_mod_is_a_different_instance() =>
        Assert.NotEqual(Of(mods: ["jei-11.6.0.jar"]), Of(mods: ["jei-11.7.0.jar"]));

    [Fact]
    public void Case_and_spacing_are_not_differences() =>
        Assert.Equal(Of(version: "1.19.2", loader: "forge", mods: ["JEI.jar"]),
                     Of(version: " 1.19.2 ", loader: "Forge", mods: [" jei.jar "]));

    [Fact]
    public void Two_plain_vanilla_instances_of_a_version_agree() =>
        Assert.Equal(Of(loader: "vanilla", loaderVersion: null),
                     Of(loader: "vanilla", loaderVersion: null));

    [Fact]
    public void It_is_short_enough_to_travel_and_long_enough_to_mean_something() =>
        Assert.Equal(16, Of(mods: ["jei.jar"]).Length);
}
