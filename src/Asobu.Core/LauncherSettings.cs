using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Which account the Play button uses.</summary>
    public string? ActiveAccountUuid { get; set; }

    [JsonIgnore]
    public bool UsesManagedJava => JavaSelection is not { Length: > 0 } || JavaSelection == "auto";

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
