using System.Globalization;
using System.Text.Json;

namespace Asobu.Core.Instances;

/// <summary>
/// One isolated Minecraft installation. This is the on-disk instance.json, which stays the
/// source of truth: losing any launcher database must never lose someone's worlds or setup.
/// </summary>
public sealed class Instance
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string MinecraftVersion { get; set; }

    /// <summary>vanilla for now; fabric, neoforge, forge and quilt slot in here.</summary>
    public string Loader { get; set; } = "vanilla";
    public string? LoaderVersion { get; set; }

    /// <summary>Free-text category shown as a filter chip. Null/empty means ungrouped.</summary>
    public string? Group { get; set; }

    /// <summary>An emoji from the curated set in IconChoices. Never a user-supplied image.</summary>
    public string Icon { get; set; } = "🌸";

    /// <summary>Applied to the game process on top of the launcher's own environment.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastPlayed { get; set; }
    public long PlaytimeSeconds { get; set; }

    // Invariant on purpose: the UI is English, so a locale decimal comma reads as a bug.
    public string PlaytimeLabel => PlaytimeSeconds switch
    {
        < 60 => "never played",
        < 3600 => $"{PlaytimeSeconds / 60} min played",
        _ => (PlaytimeSeconds / 3600.0).ToString("0.#", CultureInfo.InvariantCulture) + " h played",
    };

    /// <summary>Curated so "custom icon" never means "pick an arbitrary image file".</summary>
    public static readonly IReadOnlyList<string> IconChoices =
        ["🌸", "🎮", "⚔️", "🏹", "🧱", "⛏️", "🔥", "❄️", "🌙", "⭐", "🍄", "🐉", "🏰", "🌊", "🌲", "💎"];
}

public sealed class InstanceStore(AsobuPaths paths)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public IReadOnlyList<Instance> LoadAll()
    {
        if (!Directory.Exists(paths.Instances)) return [];

        var instances = new List<Instance>();
        foreach (var directory in Directory.EnumerateDirectories(paths.Instances))
        {
            var file = Path.Combine(directory, "instance.json");
            if (!File.Exists(file)) continue;

            try
            {
                if (JsonSerializer.Deserialize<Instance>(File.ReadAllText(file), Options) is { } instance)
                    instances.Add(instance);
            }
            catch (JsonException)
            {
                // A corrupt instance.json must not hide every other instance.
            }
        }

        return [.. instances.OrderByDescending(i => i.LastPlayed ?? i.Created)];
    }

    public Instance Create(string name, string minecraftVersion)
    {
        var instance = new Instance
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            Name = name,
            MinecraftVersion = minecraftVersion,
        };

        Directory.CreateDirectory(paths.InstanceGameDir(instance.Id));
        Save(instance);
        return instance;
    }

    public void Save(Instance instance)
    {
        Directory.CreateDirectory(paths.InstanceDir(instance.Id));
        File.WriteAllText(
            Path.Combine(paths.InstanceDir(instance.Id), "instance.json"),
            JsonSerializer.Serialize(instance, Options));
    }

    public void Delete(Instance instance)
    {
        var directory = paths.InstanceDir(instance.Id);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    /// <summary>Copies an instance's full folder under a new id, worlds and all.</summary>
    public Instance Clone(Instance source)
    {
        var clone = new Instance
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            Name = $"{source.Name} (copy)",
            MinecraftVersion = source.MinecraftVersion,
            Loader = source.Loader,
            LoaderVersion = source.LoaderVersion,
            Group = source.Group,
            Icon = source.Icon,
            EnvironmentVariables = new Dictionary<string, string>(source.EnvironmentVariables),
        };

        CopyDirectory(paths.InstanceDir(source.Id), paths.InstanceDir(clone.Id));
        Save(clone);
        return clone;
    }

    /// <summary>Zips the instance folder (instance.json plus the whole minecraft/ tree) as-is.</summary>
    public void Export(Instance instance, string destinationZipPath)
    {
        var sourceDir = paths.InstanceDir(instance.Id);
        if (File.Exists(destinationZipPath)) File.Delete(destinationZipPath);
        System.IO.Compression.ZipFile.CreateFromDirectory(sourceDir, destinationZipPath);
    }

    /// <summary>
    /// Reads back an Export()'d zip under a brand-new id, so importing the same pack twice
    /// (e.g. sharing it back to yourself) never collides with an existing instance.
    /// </summary>
    public Instance Import(string zipPath)
    {
        var staging = Path.Combine(Path.GetTempPath(), "asobu-import-" + Guid.NewGuid().ToString("n"));
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, staging);

        try
        {
            var instanceJson = Path.Combine(staging, "instance.json");
            if (!File.Exists(instanceJson))
                throw new InvalidDataException("That file isn't an exported Asobu instance.");

            var imported = JsonSerializer.Deserialize<Instance>(File.ReadAllText(instanceJson), Options)
                ?? throw new InvalidDataException("instance.json in the export is empty or corrupt.");

            imported.Id = Guid.NewGuid().ToString("n")[..12];

            var destination = paths.InstanceDir(imported.Id);
            CopyDirectory(staging, destination);
            Save(imported);
            return imported;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, directory)));

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, file)), overwrite: true);
    }
}
