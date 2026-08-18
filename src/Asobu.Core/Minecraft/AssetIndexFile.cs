using System.Text.Json.Serialization;

namespace Asobu.Core.Minecraft;

/// <summary>
/// The asset index. Assets are content-addressed by Mojang already, so every version that
/// shares a sound or texture shares one file on disk.
/// </summary>
public sealed class AssetIndexFile
{
    public Dictionary<string, AssetObject> Objects { get; init; } = [];

    /// <summary>Pre-1.7.3: the game wants a real directory tree, not a hash store.</summary>
    public bool Virtual { get; init; }

    /// <summary>Pre-1.6: the tree has to be copied into the instance's own resources folder.</summary>
    [JsonPropertyName("map_to_resources")]
    public bool MapToResources { get; init; }
}

public sealed class AssetObject
{
    public required string Hash { get; init; }
    public long Size { get; init; }

    /// <summary>Mojang shards the object store by the first two characters of the hash.</summary>
    public string RelativePath => Path.Combine(Hash[..2], Hash);

    public string Url => $"https://resources.download.minecraft.net/{Hash[..2]}/{Hash}";
}
