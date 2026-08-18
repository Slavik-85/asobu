using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asobu.Core.Minecraft;

/// <summary>
/// A Minecraft version descriptor. Vanilla versions come straight from Mojang; mod loaders
/// (Fabric, Forge, NeoForge, Quilt) publish partial documents that point at a vanilla parent
/// via <see cref="InheritsFrom"/>. Use <see cref="VersionResolver"/> to get a flattened one.
/// </summary>
public sealed class VersionJson
{
    public required string Id { get; init; }

    /// <summary>Set by mod loaders. Null for vanilla.</summary>
    public string? InheritsFrom { get; init; }

    public string? Type { get; init; }
    public string? MainClass { get; init; }

    /// <summary>Asset index id, e.g. "26", "legacy", "pre-1.6". Not always equal to AssetIndex.Id on old versions.</summary>
    public string? Assets { get; init; }

    public AssetIndexRef? AssetIndex { get; init; }
    public JavaVersionRef? JavaVersion { get; init; }

    /// <summary>Keyed by "client", "server", "client_mappings", ... Only "client" matters to us.</summary>
    public Dictionary<string, DownloadRef>? Downloads { get; init; }

    public IReadOnlyList<Library> Libraries { get; init; } = [];

    /// <summary>Structured arguments, 1.13 and later.</summary>
    public Arguments? Arguments { get; init; }

    /// <summary>Flat argument string, 1.12.2 and earlier. Mutually exclusive with <see cref="Arguments"/>.</summary>
    public string? MinecraftArguments { get; init; }

    public LoggingConfig? Logging { get; init; }
    public int? ComplianceLevel { get; init; }
    public DateTimeOffset? ReleaseTime { get; init; }
    public int? MinimumLauncherVersion { get; init; }

    public DownloadRef? ClientJar =>
        Downloads is not null && Downloads.TryGetValue("client", out var d) ? d : null;
}

public sealed class AssetIndexRef
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public string? Sha1 { get; init; }
    public long Size { get; init; }
    public long TotalSize { get; init; }
}

public sealed class JavaVersionRef
{
    /// <summary>Mojang runtime component name, e.g. "java-runtime-delta". Feeds the Java manager in M1.6.</summary>
    public string? Component { get; init; }
    public int MajorVersion { get; init; }
}

/// <summary>A single downloadable file. Fields are shared across artifacts, jars, asset indexes and log configs.</summary>
public sealed class DownloadRef
{
    /// <summary>Only present on logging configs.</summary>
    public string? Id { get; init; }

    /// <summary>Maven-relative path. Only present on library artifacts.</summary>
    public string? Path { get; init; }

    public required string Url { get; init; }
    public string? Sha1 { get; init; }
    public long Size { get; init; }
}

public sealed class Library
{
    /// <summary>Maven coordinates: group:artifact:version[:classifier].</summary>
    public required string Name { get; init; }

    public LibraryDownloads? Downloads { get; init; }
    public IReadOnlyList<Rule>? Rules { get; init; }

    /// <summary>Legacy natives mapping: os name to classifier template, e.g. "windows" -> "natives-windows-${arch}".</summary>
    public Dictionary<string, string>? Natives { get; init; }

    public ExtractRule? Extract { get; init; }

    /// <summary>Maven repository base URL. Used by loaders that omit an explicit downloads block.</summary>
    public string? Url { get; init; }
}

public sealed class LibraryDownloads
{
    public DownloadRef? Artifact { get; init; }

    /// <summary>Legacy natives payloads, keyed by classifier, e.g. "natives-windows".</summary>
    public Dictionary<string, DownloadRef>? Classifiers { get; init; }
}

public sealed class ExtractRule
{
    public IReadOnlyList<string> Exclude { get; init; } = [];
}

public sealed class LoggingConfig
{
    public LoggingClient? Client { get; init; }
}

public sealed class LoggingClient
{
    /// <summary>JVM argument template containing ${path}, e.g. "-Dlog4j.configurationFile=${path}".</summary>
    public required string Argument { get; init; }
    public required DownloadRef File { get; init; }
    public string? Type { get; init; }
}

public sealed class Arguments
{
    public IReadOnlyList<ConditionalArgument> Game { get; init; } = [];
    public IReadOnlyList<ConditionalArgument> Jvm { get; init; } = [];
}

/// <summary>
/// One entry of an argument array. Mojang mixes bare strings with rule-gated objects
/// whose "value" is itself either a string or an array of strings.
/// </summary>
[JsonConverter(typeof(ConditionalArgumentConverter))]
public sealed class ConditionalArgument
{
    public IReadOnlyList<Rule>? Rules { get; init; }
    public required IReadOnlyList<string> Values { get; init; }
}

internal sealed class ConditionalArgumentConverter : JsonConverter<ConditionalArgument>
{
    public override ConditionalArgument Read(ref Utf8JsonReader reader, Type _, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new ConditionalArgument { Values = [reader.GetString()!] };

        using var doc = JsonDocument.ParseValue(ref reader);
        var element = doc.RootElement;

        var rules = element.TryGetProperty("rules", out var rulesElement)
            ? rulesElement.Deserialize<List<Rule>>(options)
            : null;

        var values = new List<string>();
        if (element.TryGetProperty("value", out var valueElement))
        {
            if (valueElement.ValueKind == JsonValueKind.String)
                values.Add(valueElement.GetString()!);
            else
                foreach (var item in valueElement.EnumerateArray())
                    values.Add(item.GetString()!);
        }

        return new ConditionalArgument { Rules = rules, Values = values };
    }

    public override void Write(Utf8JsonWriter writer, ConditionalArgument value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Version JSON is read-only; Asobu never writes it back.");
}
