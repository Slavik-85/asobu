using System.Text.Json;
using Asobu.Core.Instances;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// Somebody who updates in the middle of an old mod hunt gets their mods back, and nothing the
/// hunt did not switch off is touched.
/// </summary>
public class LeftoverHuntTests : IDisposable
{
    private readonly string _mods = Path.Combine(Path.GetTempPath(), "asobu-hunt-" + Guid.NewGuid().ToString("N"));

    public LeftoverHuntTests() => Directory.CreateDirectory(_mods);

    public void Dispose() => Directory.Delete(_mods, recursive: true);

    private void Put(string name) => File.WriteAllBytes(Path.Combine(_mods, name), [0x50, 0x4B, 5, 6]);

    [Fact]
    public void The_hunts_mods_go_back_on_and_nothing_else_moves()
    {
        // The shape an older build saved, extra fields and all.
        const string saved = """
            { "id": "5ae999320f6a", "name": "Ham", "minecraftVersion": "1.20.1", "bisect": { "restore": ["a.jar", "c.jar"], "suspects": ["a.jar"], "off": ["a.jar"], "launches": 2, "crashes": 1 } }
            """;
        var hunt = JsonSerializer.Deserialize<Instance>(saved,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!.Bisect!;

        Put("a.jar.disabled");    // the hunt's
        Put("b.jar.disabled");    // off before the hunt started
        Put("c.jar");             // the hunt's, and a twin of itself beside it
        Put("c.jar.disabled");

        Assert.True(hunt.PutBack(_mods));

        Assert.True(File.Exists(Path.Combine(_mods, "a.jar")));
        Assert.True(File.Exists(Path.Combine(_mods, "b.jar.disabled")));
        Assert.True(File.Exists(Path.Combine(_mods, "c.jar")));
        Assert.True(File.Exists(Path.Combine(_mods, "c.jar.disabled")));
    }
}
