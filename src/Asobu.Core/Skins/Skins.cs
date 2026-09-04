using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asobu.Core.Accounts;

namespace Asobu.Core.Skins;

/// <summary>
/// Which arms a skin was drawn for. Not something a PNG can say about itself — the two are the
/// same size and differ only in whether the arm is three pixels wide or four — so it travels
/// alongside the file everywhere it goes.
/// </summary>
public enum SkinModel
{
    Classic,
    Slim,
}

/// <summary>A skin in the local library.</summary>
public sealed record SavedSkin(string File, string Name, SkinModel Model, DateTimeOffset Added)
{
    [JsonIgnore] public string Path { get; init; } = "";
}

/// <summary>
/// The skins somebody has kept.
///
/// Files on disk with a small index beside them, rather than a database: a skin is a 64×64 PNG
/// and people want to be able to open the folder and see them. The index carries the one thing
/// the PNG cannot — whether it was drawn for slim arms — and the name they gave it.
/// </summary>
public sealed class SkinLibrary(AsobuPaths paths)
{
    public string Folder => System.IO.Path.Combine(paths.Root, "skins");

    private string IndexFile => System.IO.Path.Combine(Folder, "skins.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public IReadOnlyList<SavedSkin> All()
    {
        try
        {
            if (!File.Exists(IndexFile)) return [];

            var saved = JsonSerializer.Deserialize<List<SavedSkin>>(File.ReadAllText(IndexFile), Options) ?? [];

            // Anything the index lists but the folder no longer has was deleted from underneath
            // us, which is allowed — it is a folder of PNGs and people tidy those.
            return [.. saved
                .Select(skin => skin with { Path = System.IO.Path.Combine(Folder, skin.File) })
                .Where(skin => File.Exists(skin.Path))
                .OrderByDescending(skin => skin.Added)];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return [];
        }
    }

    /// <summary>Keeps a skin, under a file name of our choosing so theirs cannot collide.</summary>
    public SavedSkin Save(byte[] png, string name, SkinModel model)
    {
        SkinPng.Validate(png);
        Directory.CreateDirectory(Folder);

        var saved = new SavedSkin($"{Guid.NewGuid():N}.png", Tidy(name), model, DateTimeOffset.UtcNow);
        var path = System.IO.Path.Combine(Folder, saved.File);

        File.WriteAllBytes(path, png);

        Write([.. Stored(), saved]);

        return saved with { Path = path };
    }

    public SavedSkin Import(string sourcePath, SkinModel model) =>
        Save(File.ReadAllBytes(sourcePath), System.IO.Path.GetFileNameWithoutExtension(sourcePath), model);

    public void Rename(SavedSkin skin, string name) =>
        Write([.. Stored().Select(s => s.File == skin.File ? s with { Name = Tidy(name) } : s)]);

    public void Remove(SavedSkin skin)
    {
        Write([.. Stored().Where(s => s.File != skin.File)]);

        try
        {
            var path = System.IO.Path.Combine(Folder, skin.File);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Held open by a preview somewhere. It is out of the index, which is what matters.
        }
    }

    private List<SavedSkin> Stored()
    {
        try
        {
            return File.Exists(IndexFile)
                ? JsonSerializer.Deserialize<List<SavedSkin>>(File.ReadAllText(IndexFile), Options) ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return [];
        }
    }

    private void Write(List<SavedSkin> skins)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(IndexFile, JsonSerializer.Serialize(skins, Options));
    }

    private static string Tidy(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? "Skin" : trimmed[..Math.Min(60, trimmed.Length)];
    }
}

/// <summary>What a skin file has to be before anything will take it.</summary>
public static class SkinPng
{
    /// <summary>
    /// Reads the size straight out of the PNG header rather than decoding the image. The first
    /// chunk of a PNG is always IHDR, and its width and height are the two big-endian ints at
    /// byte 16 — which is all that needs checking, and needs no image library to do it.
    /// </summary>
    public static (int Width, int Height) Size(byte[] png)
    {
        if (png.Length < 24 || BinaryPrimitives.ReadUInt64BigEndian(png) != 0x89504E470D0A1A0AUL)
            throw new SkinException("That file isn't a PNG.");

        return ((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));
    }

    /// <summary>
    /// 64×64, or the 64×32 the game used before 1.8 — which it still accepts, and which plenty of
    /// old skins people have kept are still in.
    /// </summary>
    public static void Validate(byte[] png)
    {
        var (width, height) = Size(png);

        if (width != 64 || (height != 64 && height != 32))
            throw new SkinException($"A skin has to be 64×64. That one is {width}×{height}.");
    }
}

public sealed class SkinException(string message) : Exception(message);

/// <summary>
/// Everything that talks to Mojang about skins: whose is whose, and changing your own.
///
/// All of it is Mojang's own public API. Looking a player up needs no key and no account — it is
/// the same route the game itself uses to find out what to draw — and changing a skin needs the
/// signed-in account's own token, which is the only thing that could possibly authorise it.
/// </summary>
public sealed partial class SkinService(HttpClient http)
{
    private const string LookUpUrl = "https://api.minecraftservices.com/minecraft/profile/lookup/name/";
    private const string ProfileUrl = "https://sessionserver.mojang.com/session/minecraft/profile/";
    private const string SkinsUrl = "https://api.minecraftservices.com/minecraft/profile/skins";
    private const string ActiveSkinUrl = "https://api.minecraftservices.com/minecraft/profile/skins/active";

    private const string GalleryUrl = "https://api.mineskin.org/v2/skins";
    private const string TextureUrl = "https://textures.minecraft.net/texture/";

    /// <summary>
    /// A shelf of skins to look through without having to know a name first.
    ///
    /// From MineSkin's public API rather than a skin site's web page. That is a deliberate
    /// second choice: the obvious galleries are behind bot protection that answers a program
    /// with a challenge page no matter how it asks, and the only ways through it are the ones
    /// that amount to pretending not to be a program. This is an API meant to be called, and
    /// what it returns are Mojang texture hashes — the real 64×64 files — so a skin from here
    /// can be worn rather than only admired.
    /// </summary>
    public async Task<GalleryPageResult> GalleryAsync(
        string? after = null, int count = 30, CancellationToken cancellationToken = default)
    {
        try
        {
            // Paged by a cursor rather than a number, and the answer says what the next one is.
            var url = $"{GalleryUrl}?size={Math.Clamp(count, 1, 64)}"
                + (after is { Length: > 0 } ? $"&after={Uri.EscapeDataString(after)}" : "");

            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new GalleryPageResult([], null);

            var page = await response.Content
                .ReadFromJsonAsync<GalleryPage>(cancellationToken)
                .ConfigureAwait(false);

            return new GalleryPageResult(
                [.. (page?.Skins ?? [])
                    .Where(skin => skin.Texture is { Length: > 16 })
                    .Select(skin => new GallerySkin(skin.Texture!, skin.Name))
                    .DistinctBy(skin => skin.Texture)],
                page?.Pagination?.Next?.After);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            // A shelf that would not load. The lookup box beside it still works, and saying so is
            // the caller's job rather than this one's.
            return new GalleryPageResult([], null);
        }
    }

    /// <summary>The real 64×64 behind a gallery entry, from Mojang's own texture host.</summary>
    public Task<byte[]> TextureAsync(string hash, CancellationToken cancellationToken = default) =>
        http.GetByteArrayAsync(TextureUrl + hash, cancellationToken);

    private sealed class GalleryPage
    {
        [JsonPropertyName("skins")] public List<GalleryEntry>? Skins { get; init; }
        [JsonPropertyName("pagination")] public Pagination? Pagination { get; init; }
    }

    private sealed class Pagination
    {
        [JsonPropertyName("next")] public Cursor? Next { get; init; }
    }

    private sealed class Cursor
    {
        [JsonPropertyName("after")] public string? After { get; init; }
    }

    private sealed class GalleryEntry
    {
        [JsonPropertyName("texture")] public string? Texture { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    /// <summary>The player of that name, or null if nobody has it.</summary>
    public async Task<PlayerSkin?> FindAsync(string username, CancellationToken cancellationToken = default)
    {
        var name = username.Trim();
        if (name.Length == 0) return null;

        using var response = await http.GetAsync(LookUpUrl + Uri.EscapeDataString(name), cancellationToken)
            .ConfigureAwait(false);

        // 404 for a name nobody has, and 400 for one no account could be called. Both mean the
        // same thing to somebody typing in a box, and neither is an error worth showing.
        if (!response.IsSuccessStatusCode) return null;

        var found = await response.Content.ReadFromJsonAsync<NamedProfile>(cancellationToken).ConfigureAwait(false);
        if (found?.Id is not { Length: > 0 } id) return null;

        return await OfUuidAsync(id, found.Name ?? name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The skin a uuid is wearing, read from the profile the session server serves.</summary>
    public async Task<PlayerSkin?> OfUuidAsync(
        string uuid, string username, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(ProfileUrl + uuid.Replace("-", ""), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;

        var profile = await response.Content.ReadFromJsonAsync<SessionProfile>(cancellationToken).ConfigureAwait(false);

        // The textures are a base64 blob of JSON inside the profile rather than fields on it.
        var blob = profile?.Properties?.FirstOrDefault(p => p.Name == "textures")?.Value;
        if (blob is not { Length: > 0 }) return null;

        TextureSet? textures;
        try
        {
            textures = JsonSerializer.Deserialize<TextureSet>(Convert.FromBase64String(blob));
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }

        if (textures?.Textures?.Skin?.Url is not { Length: > 0 } url) return null;

        return new PlayerSkin(
            username,
            uuid,
            url,

            // Mojang says "slim" here and says nothing at all for classic.
            textures.Textures.Skin.Metadata?.Model == "slim" ? SkinModel.Slim : SkinModel.Classic);
    }

    public async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        // Mojang still hands these out as http. Asking for https gets the same file from the same
        // host, and there is no reason to fetch it in the clear.
        var secure = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url["http://".Length..]
            : url;

        return await http.GetByteArrayAsync(secure, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts a skin on the signed-in account.
    ///
    /// The account's own access token is the authorisation, so this only ever changes the skin of
    /// whoever is signed in to Asobu. An offline account has no token and no Mojang profile to
    /// change, which is why the caller has to have resolved a real session first.
    /// </summary>
    public async Task ApplyAsync(
        MinecraftSession session, byte[] png, SkinModel model, CancellationToken cancellationToken = default)
    {
        SkinPng.Validate(png);

        if (session.UserType == "legacy")
            throw new SkinException("Offline accounts have no Mojang profile, so their skin can't be changed.");

        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        using var body = new MultipartFormDataContent
        {
            { new StringContent(model == SkinModel.Slim ? "slim" : "classic"), "variant" },
            { file, "file", "skin.png" },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, SkinsUrl) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            throw new SkinException($"Mojang wouldn't take the skin ({(int)response.StatusCode})."
                + (detail is { Length: > 0 and < 300 } ? " " + detail.Trim() : ""));
        }
    }

    /// <summary>Back to the one Mojang gives an account that has never set its own.</summary>
    public async Task ResetAsync(MinecraftSession session, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ActiveSkinUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new SkinException($"Mojang wouldn't reset the skin ({(int)response.StatusCode}).");
    }

    private sealed class NamedProfile
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    private sealed class SessionProfile
    {
        [JsonPropertyName("properties")] public List<ProfileProperty>? Properties { get; init; }
    }

    private sealed class ProfileProperty
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("value")] public string? Value { get; init; }
    }

    private sealed class TextureSet
    {
        [JsonPropertyName("textures")] public Textures? Textures { get; init; }
    }

    private sealed class Textures
    {
        [JsonPropertyName("SKIN")] public Texture? Skin { get; init; }
    }

    private sealed class Texture
    {
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("metadata")] public TextureMetadata? Metadata { get; init; }
    }

    private sealed class TextureMetadata
    {
        [JsonPropertyName("model")] public string? Model { get; init; }
    }
}

/// <summary>A player and the skin they are wearing.</summary>
public sealed record PlayerSkin(string Username, string Uuid, string Url, SkinModel Model);

/// <summary>
/// One skin on the public gallery. Which arms it was drawn for is not in the listing, so it is
/// worked out from the file once that arrives.
/// </summary>
public sealed record GallerySkin(string Texture, string? Name);

/// <summary>A page of the gallery, and where the next one starts.</summary>
public sealed record GalleryPageResult(IReadOnlyList<GallerySkin> Skins, string? Next);
