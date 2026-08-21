using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asobu.Core.Instances;

/// <summary>
/// One isolated Minecraft installation. This is the on-disk instance.json, which stays the
/// source of truth: losing any launcher database must never lose someone's worlds or setup.
///
/// It announces its own changes. The alternative — telling the screen that "the selected
/// instance" changed after editing one — does not work: the reference is the same one it was
/// before, so nothing downstream of it re-reads, and a renamed instance keeps its old name until
/// the page is opened again. Only the fields anything actually edits do this; the rest are
/// written once when the instance is made.
///
/// System.ComponentModel, so this costs the model nothing beyond the framework it already has.
/// </summary>
public sealed class Instance : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises for the property that changed and for anything computed from it — a renamed icon
    /// has to move IconEmoji and HasCustomIcon along with it, since those are what the screen
    /// is really bound to.
    /// </summary>
    private void Set<T>(ref T field, T value, string[]? also = null, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;

        field = value;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        foreach (var derived in also ?? []) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(derived));
    }

    private string _name = "";
    private string _icon = "🌸";
    private string? _banner;
    private string? _group;
    private long _playtimeSeconds;

    public required string Id { get; set; }

    public required string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public required string MinecraftVersion { get; set; }

    /// <summary>vanilla or fabric today; neoforge, forge and quilt slot in here.</summary>
    public string Loader { get; set; } = "vanilla";
    public string? LoaderVersion { get; set; }

    /// <summary>
    /// A Modrinth project id to keep installed, or null. Held as a wish rather than a one-off
    /// action so a missing jar is re-fetched instead of silently staying gone.
    /// </summary>
    public string? PerformanceMod { get; set; }

    [JsonIgnore]
    public bool UsesFabric => Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool UsesQuilt => Loader.Equals("quilt", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fabric and Quilt share a metadata service shape and a profile format, so everything from
    /// installing to launching treats them alike — only which service is asked differs.
    /// </summary>
    [JsonIgnore]
    public bool UsesFabricFamily => UsesFabric || UsesQuilt;

    /// <summary>Forge and NeoForge share an installer format, so most code treats them alike.</summary>
    [JsonIgnore]
    public bool UsesForgeFamily =>
        Loader.Equals("forge", StringComparison.OrdinalIgnoreCase) ||
        Loader.Equals("neoforge", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsModded => !Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The loader's name as people write it. A map rather than capitalising the stored string,
    /// which would render NeoForge as "Neoforge".
    /// </summary>
    [JsonIgnore]
    public string LoaderName => Loader.ToLowerInvariant() switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        _ => "Vanilla",
    };

    /// <summary>"Vanilla" or "Forge 47.4.10", for the one line under an instance's name.</summary>
    [JsonIgnore]
    public string LoaderLabel =>
        IsModded && LoaderVersion is { Length: > 0 } version ? $"{LoaderName} {version}" : LoaderName;

    /// <summary>Free-text category shown as a filter chip. Null/empty means ungrouped.</summary>
    public string? Group
    {
        get => _group;
        set => Set(ref _group, value, [nameof(IsPinned), nameof(PinLabel)]);
    }

    /// <summary>
    /// The group pinning uses. Pinning really is just a group — nothing else about the instance
    /// changes — which means it survives an export, reads correctly in any other launcher that
    /// looks at the file, and needs no second concept anywhere.
    /// </summary>
    public const string PinnedGroup = "Pinned";

    [JsonIgnore]
    public bool IsPinned => PinnedGroup.Equals(Group, StringComparison.OrdinalIgnoreCase);

    /// <summary>What the context menu offers, since one item does both.</summary>
    [JsonIgnore]
    public string PinLabel => IsPinned ? "Unpin" : "Pin";

    /// <summary>
    /// Either an emoji from the curated set in <see cref="IconChoices"/>, or
    /// "custom:&lt;file&gt;" naming an image sitting in this instance's own folder.
    /// </summary>
    public string Icon
    {
        get => _icon;
        set => Set(ref _icon, value, [nameof(HasCustomIcon), nameof(IconEmoji), nameof(IconImagePath)]);
    }

    /// <summary>
    /// The name of this instance's own folder, filled in by <see cref="InstanceStore"/>. Follows
    /// the instance's name so the folder is findable by a human, minus anything a file system
    /// will not take — see <see cref="InstanceStore.FolderNameFor"/>.
    ///
    /// Not the id, and not serialised. The id is what the rest of the launcher holds on to and
    /// never changes; this moves whenever the instance is renamed, and is read back off the disk
    /// on load, so an instance made before folders had names keeps the folder it has.
    /// </summary>
    [JsonIgnore]
    public string Folder { get; set; } = "";

    /// <summary>
    /// Absolute path to this instance's folder, filled in by <see cref="InstanceStore"/> when it
    /// loads or creates the instance. Not serialised: where Asobu keeps its files is a fact about
    /// this machine today, not something an instance.json should carry between them.
    /// </summary>
    [JsonIgnore]
    public string? FolderPath { get; set; }

    [JsonIgnore]
    public bool HasCustomIcon => Icon.StartsWith(CustomIconPrefix, StringComparison.Ordinal);

    /// <summary>The emoji to draw, or a placeholder while a custom icon is what's really shown.</summary>
    [JsonIgnore]
    public string IconEmoji => HasCustomIcon ? "🖼" : Icon;

    /// <summary>
    /// Where the custom icon lives, or null when this instance uses an emoji. Exposed as a path
    /// rather than a decoded image so the model stays clear of the UI toolkit.
    /// </summary>
    [JsonIgnore]
    public string? IconImagePath =>
        HasCustomIcon && FolderPath is { Length: > 0 } folder
            ? Path.Combine(folder, Icon[CustomIconPrefix.Length..])
            : null;

    /// <summary>
    /// Scenery behind the instance's hero. Null means "pick one from the id" — the original
    /// behaviour, kept as the default so instances made before this existed look unchanged.
    /// Otherwise it is "builtin:&lt;file&gt;" naming one of the bundled images, or
    /// "custom:&lt;file&gt;" naming an image sitting in this instance's own folder.
    /// </summary>
    public string? Banner
    {
        get => _banner;
        set => Set(ref _banner, value);
    }

    /// <summary>Applied to the game process on top of the launcher's own environment.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];

    // ---- Per-instance overrides of the launcher settings.
    //
    // Nullable rather than a copy of whatever the global value happened to be when the instance
    // was made: null means "follow the launcher", so raising the default memory later still moves
    // every instance that never asked for something different.

    public int? MinMemoryMb { get; set; }
    public int? MaxMemoryMb { get; set; }

    /// <summary>Null to follow the launcher; otherwise "auto" or a path to a java executable.</summary>
    public string? JavaSelection { get; set; }

    public string? ExtraJvmArguments { get; set; }

    /// <summary>True when this instance runs on anything other than the launcher defaults.</summary>
    [JsonIgnore]
    public bool HasOverrides =>
        MinMemoryMb is not null || MaxMemoryMb is not null ||
        JavaSelection is not null || ExtraJvmArguments is not null;

    /// <summary>
    /// Builds of mods that crashed this instance, by file name.
    ///
    /// Kept so an automatic fix cannot offer back something already known not to work. Without it
    /// a mod with two builds, both tagged for this version and neither actually running on it,
    /// swaps from one to the other and back for as long as anybody keeps pressing — which is
    /// exactly what happened. Remembered on the instance rather than in the session, because each
    /// crash is a new session.
    /// </summary>
    public List<string> CrashedBuilds { get; set; } = [];

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastPlayed { get; set; }
    public long PlaytimeSeconds
    {
        get => _playtimeSeconds;
        set => Set(ref _playtimeSeconds, value, [nameof(PlaytimeLabel)]);
    }

    // Invariant on purpose: the UI is English, so a locale decimal comma reads as a bug.
    public string PlaytimeLabel => PlaytimeSeconds switch
    {
        < 60 => "never played",
        < 3600 => $"{PlaytimeSeconds / 60} min played",
        _ => (PlaytimeSeconds / 3600.0).ToString("0.#", CultureInfo.InvariantCulture) + " h played",
    };

    /// <summary>
    /// The longest an instance's name may be. Any character at all is allowed up to it — emoji,
    /// punctuation, another script — because the name is the instance's, not the folder's. What
    /// a file system will not accept is dealt with when the folder is named, not by narrowing
    /// what people are allowed to call things.
    /// </summary>
    public const int MaxNameLength = 32;

    /// <summary>The quick picks, for people who don't want to go and find a picture.</summary>
    public static readonly IReadOnlyList<string> IconChoices =
        ["🌸", "🎮", "⚔️", "🏹", "🧱", "⛏️", "🔥", "❄️", "🌙", "⭐", "🍄", "🐉", "🏰", "🌊", "🌲", "💎"];

    /// <summary>Token prefixes, shared so the launcher and the UI can't drift apart.</summary>
    public const string BuiltInBannerPrefix = "builtin:";
    public const string CustomBannerPrefix = "custom:";
    public const string CustomIconPrefix = "custom:";
}

public sealed class InstanceStore(AsobuPaths paths)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Characters no Windows path may contain, plus the ones that are legal but ask for trouble:
    /// a leading dot hides the folder on the other two platforms Avalonia runs on.
    /// </summary>
    private static readonly char[] Illegal =
        [.. Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])];

    /// <summary>
    /// Names Windows will not give a folder however it is spelled, reserved for devices since
    /// DOS. "CON" is not a legal folder name; neither is "con.txt".
    /// </summary>
    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// The folder an instance of this name gets: the name itself, minus whatever the file system
    /// will not take. Nothing is substituted for what is dropped — "Hello: World" becomes
    /// "Hello World" rather than "Hello_ World", because the point is a folder someone can
    /// recognise, and underscores standing in for punctuation help nobody.
    ///
    /// Falls back to "Instance" when a name survives none of this, which a name made entirely of
    /// slashes or emoji legitimately can.
    /// </summary>
    public static string FolderNameFor(string name)
    {
        var kept = new string([.. name
            .Where(c => !char.IsControl(c) && !Illegal.Contains(c))]);

        // Windows silently drops trailing dots and spaces, which would leave the folder under a
        // name nothing afterwards can find.
        kept = kept.Trim().TrimEnd('.', ' ').Trim();

        if (kept.Length == 0) return "Instance";

        if (Reserved.Contains(Path.GetFileNameWithoutExtension(kept), StringComparer.OrdinalIgnoreCase))
            kept += " instance";

        return kept.Length > Instance.MaxNameLength ? kept[..Instance.MaxNameLength].TrimEnd() : kept;
    }

    /// <summary>
    /// The same, made unique. Two instances may share a name — people do that deliberately, with
    /// groups to tell them apart — but two folders cannot, so the second gets a number.
    /// </summary>
    private string UniqueFolderFor(string name, string? keeping = null)
    {
        var wanted = FolderNameFor(name);

        if (Available(wanted, keeping)) return wanted;

        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{wanted} ({n})";
            if (Available(candidate, keeping)) return candidate;
        }

        // A thousand folders of one name is not a case worth a better answer than a unique one.
        return $"{wanted} ({Guid.NewGuid().ToString("n")[..6]})";
    }

    private bool Available(string folder, string? keeping) =>
        string.Equals(folder, keeping, StringComparison.OrdinalIgnoreCase)
        || !Directory.Exists(Path.Combine(paths.Instances, folder));

    /// <summary>
    /// Renames the instance, and moves its folder to match. The folder is moved rather than left
    /// behind under the old name: it is the one thing a person goes looking for outside the
    /// launcher, and one that still says "Test Pack" after the instance was renamed is worse
    /// than no naming convention at all.
    ///
    /// A move that fails is not a rename that fails — the game's files are exactly where the
    /// instance says they are either way, so the name still changes and the folder catches up
    /// the next time it can.
    /// </summary>
    public void Rename(Instance instance, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        if (name.Length > Instance.MaxNameLength) name = name[..Instance.MaxNameLength];

        var from = paths.InstanceDir(instance.Folder);
        var to = UniqueFolderFor(name, keeping: instance.Folder);

        instance.Name = name;

        if (!string.Equals(to, instance.Folder, StringComparison.Ordinal) && Directory.Exists(from))
        {
            try
            {
                Directory.Move(from, paths.InstanceDir(to));
                instance.Folder = to;
            }
            catch (IOException)
            {
                // Something inside is open — the game, an editor, a virus scanner. The name is
                // still the name; only the folder stays where it was.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Save(instance);
    }

    /// <summary>
    /// The last read of the folder. Every page that shows instances calls LoadAll when it is
    /// opened — the library, Explore, Browse, the install picker — and each of those was reading
    /// and parsing every instance.json again. Nothing changes it but this class, so this class
    /// can remember it.
    /// </summary>
    private IReadOnlyList<Instance>? _cached;

    /// <summary>
    /// Forgets the list, so the next read picks up what just changed. Called from everything
    /// here that writes: forgetting to would leave a renamed instance showing its old name until
    /// the launcher restarted.
    /// </summary>
    private void Invalidate() => _cached = null;

    public IReadOnlyList<Instance> LoadAll()
    {
        if (_cached is { } remembered) return remembered;

        if (!Directory.Exists(paths.Instances)) return _cached = [];

        var instances = new List<Instance>();
        foreach (var directory in Directory.EnumerateDirectories(paths.Instances))
        {
            var file = Path.Combine(directory, "instance.json");
            if (!File.Exists(file)) continue;

            try
            {
                if (JsonSerializer.Deserialize<Instance>(File.ReadAllText(file), Options) is { } instance)
                {
                    // Whatever the folder is actually called, which for anything made before
                    // folders were named after instances is still the id.
                    instance.Folder = Path.GetFileName(directory);
                    instances.Add(Track(instance));
                }
            }
            catch (JsonException)
            {
                // A corrupt instance.json must not hide every other instance.
            }
        }

        return _cached = [.. instances.OrderByDescending(i => i.LastPlayed ?? i.Created)];
    }

    public Instance Create(
        string name,
        string minecraftVersion,
        string loader = "vanilla",
        string? loaderVersion = null,
        string? performanceMod = null)
    {
        if (name.Length > Instance.MaxNameLength) name = name[..Instance.MaxNameLength];

        var instance = new Instance
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            Name = name,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            PerformanceMod = performanceMod,

            // Named after the instance rather than after its id: this is the folder people open
            // to drop a mod in or copy a world out, and a twelve-character hex string tells them
            // nothing about which instance they are looking at.
            Folder = UniqueFolderFor(name),
        };

        Directory.CreateDirectory(paths.InstanceGameDir(instance.Folder));
        Save(instance);
        return instance;
    }

    public void Save(Instance instance)
    {
        Invalidate();

        // An instance that reached here without one — imported, or hand-made in a test — still
        // has to land somewhere, and its own name is the right somewhere.
        if (instance.Folder.Length == 0) instance.Folder = UniqueFolderFor(instance.Name);

        Track(instance);
        Directory.CreateDirectory(paths.InstanceDir(instance.Folder));
        File.WriteAllText(
            Path.Combine(paths.InstanceDir(instance.Folder), "instance.json"),
            JsonSerializer.Serialize(instance, Options));
    }

    /// <summary>
    /// Tells an instance where it lives. Every path that hands one out runs through here, so no
    /// caller can end up holding an instance whose custom icon resolves to nowhere.
    /// </summary>
    private Instance Track(Instance instance)
    {
        instance.FolderPath = paths.InstanceDir(instance.Folder);
        return instance;
    }

    public void Delete(Instance instance)
    {
        Invalidate();

        var directory = paths.InstanceDir(instance.Folder);
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
            Banner = source.Banner,
            PerformanceMod = source.PerformanceMod,
            EnvironmentVariables = new Dictionary<string, string>(source.EnvironmentVariables),
            MinMemoryMb = source.MinMemoryMb,
            MaxMemoryMb = source.MaxMemoryMb,
            JavaSelection = source.JavaSelection,
            ExtraJvmArguments = source.ExtraJvmArguments,
        };

        clone.Folder = UniqueFolderFor(clone.Name);

        CopyDirectory(paths.InstanceDir(source.Folder), paths.InstanceDir(clone.Folder));
        Save(clone);
        return clone;
    }

    /// <summary>
    /// Points the instance's banner at a picture of the user's own. Copied rather than referenced
    /// in place: an export then carries its own artwork, and moving or deleting the original
    /// later can't blank the page.
    /// </summary>
    public void SetCustomBanner(Instance instance, string sourceImagePath)
    {
        instance.Banner = Instance.CustomBannerPrefix + CopyArtwork(instance, sourceImagePath, "banner");
        Save(instance);
    }

    /// <summary>The same, for the tile shown in the library and at the top of the page.</summary>
    public void SetCustomIcon(Instance instance, string sourceImagePath)
    {
        instance.Icon = Instance.CustomIconPrefix + CopyArtwork(instance, sourceImagePath, "icon");
        Save(instance);
    }

    /// <summary>Drops a custom icon's file when the instance goes back to an emoji.</summary>
    public void ClearCustomIcon(Instance instance) => RemoveArtwork(instance, "icon");

    private string CopyArtwork(Instance instance, string sourceImagePath, string baseName)
    {
        var extension = Path.GetExtension(sourceImagePath);
        if (extension.Length is 0 or > 8) extension = ".png";

        var directory = paths.InstanceDir(instance.Folder);
        Directory.CreateDirectory(directory);

        RemoveArtwork(instance, baseName);

        var fileName = baseName + extension;
        File.Copy(sourceImagePath, Path.Combine(directory, fileName), overwrite: true);
        return fileName;
    }

    /// <summary>
    /// One picture per slot. A previous pick saved under a different extension would otherwise
    /// sit there forever, invisible, riding along in every export.
    /// </summary>
    private void RemoveArtwork(Instance instance, string baseName)
    {
        var directory = paths.InstanceDir(instance.Folder);
        if (!Directory.Exists(directory)) return;

        foreach (var stale in Directory.EnumerateFiles(directory, baseName + ".*"))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // Still open somewhere. A later copy overwrites it; worst case it lingers.
            }
        }
    }

    /// <summary>Zips the instance folder (instance.json plus the whole minecraft/ tree) as-is.</summary>
    public void Export(Instance instance, string destinationZipPath)
    {
        var sourceDir = paths.InstanceDir(instance.Folder);
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
            imported.Folder = UniqueFolderFor(imported.Name);

            var destination = paths.InstanceDir(imported.Folder);
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
