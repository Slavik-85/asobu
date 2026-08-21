using System.Text.Json;
using System.Text.Json.Serialization;
using Asobu.Core.Accounts;
using Asobu.Core.Instances;
using Asobu.Core.Launch;

namespace Asobu.Core;

[JsonConverter(typeof(JsonStringEnumConverter<GpuPreference>))]
public enum GpuPreference
{
    Auto,
    PowerSaving,
    HighPerformance,
}

/// <summary>Global launcher preferences. Deliberately few: the defaults should just work.</summary>
public sealed class LauncherSettings
{
    /// <summary>
    /// Size each instance's heap from what that instance actually is. On by default, because one
    /// number for everything is always wrong twice: too much for vanilla, too little for a pack.
    /// Turning it off falls back to the two figures below.
    /// </summary>
    public bool AutomaticMemory { get; set; } = true;

    public int MinMemoryMb { get; set; } = 1024;
    public int MaxMemoryMb { get; set; } = 4096;

    public GpuPreference Gpu { get; set; } = GpuPreference.HighPerformance;

    /// <summary>"auto" to let Asobu manage Java, otherwise an absolute path to a java executable.</summary>
    public string JavaSelection { get; set; } = "auto";

    public string? ExtraJvmArguments { get; set; }

    /// <summary>
    /// Azure app registration id for Microsoft sign-in. Kept in settings rather than baked into
    /// the binary: Minecraft auth needs an id Mojang has approved for this launcher, and a
    /// desktop executable is not a place to hide anything.
    /// </summary>
    public string? MicrosoftClientId { get; set; }

    /// <summary>
    /// Which route new Microsoft sign-ins take. Device code needs no app registration and works
    /// today; the registered route is the honest one and becomes available once Mojang approves
    /// the client id below. Existing accounts remember how they were added either way.
    /// </summary>
    public AuthMethod MicrosoftSignIn { get; set; } = AuthMethod.DeviceCode;

    /// <summary>
    /// A CurseForge API key, which their catalogue will not answer without. Kept here rather than
    /// compiled in because CurseForge issue keys per application to that application's owner, and
    /// their terms do not allow one to be redistributed inside a client. Prism ships its own key
    /// for the same reason — it is Prism's, issued to Prism.
    /// </summary>
    public string? CurseForgeApiKey { get; set; }

    /// <summary>
    /// Library group headers the user has collapsed. Stored by name: a group is only ever a
    /// string on an instance, so there is no id to key this on.
    /// </summary>
    public List<string> CollapsedGroups { get; set; } = [];

    /// <summary>
    /// The order the library's group bands are shown in, once somebody has dragged one. Empty
    /// until then, which is what keeps the old arrangement — Ungrouped, then alphabetical — for
    /// anyone who never reorders anything.
    ///
    /// Pinned is never in here. It is always the first band and cannot be dragged, a pin being
    /// a request to be at the top rather than a group somebody made.
    ///
    /// Names that no longer match a band are harmless and are left alone: emptying a group and
    /// filling it again later should not cost it its place.
    /// </summary>
    public List<string> GroupOrder { get; set; } = [];

    /// <summary>Which account the Play button uses.</summary>
    public string? ActiveAccountUuid { get; set; }

    /// <summary>
    /// Whether the welcome has been through. False on a fresh install, and also false for
    /// anyone upgrading from a build that predates it — so the launcher treats an existing
    /// account or instance as proof of a returning user rather than showing them a welcome
    /// they have already outgrown. See AsobuLauncher's constructor.
    /// </summary>
    public bool IntroCompleted { get; set; }

    /// <summary>
    /// Whether the tour has been offered. Offered, not taken: someone who said no should not
    /// be asked again on every launch.
    /// </summary>
    public bool TourOffered { get; set; }

    [JsonIgnore]
    public bool UsesManagedJava => JavaSelection is not { Length: > 0 } || JavaSelection == "auto";

    /// <summary>
    /// These settings with one instance's own overrides folded in, for use at launch. Only the
    /// fields an instance can override are taken from it; everything else stays global.
    /// </summary>
    public LauncherSettings ForInstance(Instance instance, AsobuPaths paths)
    {
        var (min, max) = ResolveMemory(instance, paths);

        return new LauncherSettings
        {
            AutomaticMemory = false,
            MinMemoryMb = min,
            MaxMemoryMb = max,
            Gpu = Gpu,
            JavaSelection = instance.JavaSelection ?? JavaSelection,
            ExtraJvmArguments = instance.ExtraJvmArguments ?? ExtraJvmArguments,
            MicrosoftClientId = MicrosoftClientId,
            ActiveAccountUuid = ActiveAccountUuid,
        };
    }

    /// <summary>
    /// Three tiers, most specific first: what the instance asked for, then the automatic figure
    /// sized to that instance, then the fixed pair from the settings page.
    /// </summary>
    private (int Min, int Max) ResolveMemory(Instance instance, AsobuPaths paths)
    {
        if (instance.MinMemoryMb is not null || instance.MaxMemoryMb is not null)
        {
            var max = instance.MaxMemoryMb ?? MaxMemoryMb;

            // A maximum below the minimum hands java -Xms above -Xmx, which refuses to start
            // at all. Clamp here rather than fail on launch.
            return (Math.Min(instance.MinMemoryMb ?? MinMemoryMb, max), max);
        }

        if (AutomaticMemory)
        {
            var automatic = MemoryPlanner.MaxMemoryMbFor(paths, instance);
            return (MemoryPlanner.MinMemoryMbFor(automatic), automatic);
        }

        return (Math.Min(MinMemoryMb, MaxMemoryMb), MaxMemoryMb);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static LauncherSettings Load(AsobuPaths paths)
    {
        try
        {
            if (File.Exists(paths.SettingsFile))
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(paths.SettingsFile), Options)
                       ?? new LauncherSettings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Corrupt settings fall back to defaults rather than blocking the launcher.
        }

        return new LauncherSettings();
    }

    public void Save(AsobuPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>Total physical RAM, so the memory slider can offer a sane ceiling.</summary>
    public static int SystemMemoryMb() =>
        (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
}
