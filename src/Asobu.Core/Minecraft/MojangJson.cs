using System.Text.Json;

namespace Asobu.Core.Minecraft;

/// <summary>Shared serializer settings for every piece of Mojang metadata.</summary>
public static class MojangJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
