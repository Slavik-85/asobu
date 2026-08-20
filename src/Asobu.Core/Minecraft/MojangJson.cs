using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asobu.Core.Minecraft;

/// <summary>Shared serializer settings for every piece of Mojang and loader metadata.</summary>
public static class MojangJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new LenientDateTimeOffsetConverter() },
    };
}

/// <summary>
/// Accepts the timestamps loaders actually emit, not just the ones Mojang does.
///
/// System.Text.Json only reads RFC 3339, where the offset carries a colon — "+00:00". Fabric
/// writes "+0000", which is still valid ISO 8601 and which .NET's own parser handles fine. Every
/// Fabric profile was failing to deserialise on that one character.
/// </summary>
internal sealed class LenientDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TryGetDateTimeOffset(out var value)) return value;

        return DateTimeOffset.TryParse(
            reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            // A timestamp is decoration on a version document; a bad one must not sink the launch.
            : default;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
