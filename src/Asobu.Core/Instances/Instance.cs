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

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastPlayed { get; set; }
    public long PlaytimeSeconds { get; set; }

    public string PlaytimeLabel => PlaytimeSeconds switch
    {
        < 60 => "never played",
        < 3600 => $"{PlaytimeSeconds / 60} min played",
        _ => $"{PlaytimeSeconds / 3600.0:0.#} h played",
    };
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
}
