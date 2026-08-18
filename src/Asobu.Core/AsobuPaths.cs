namespace Asobu.Core;

/// <summary>
/// Every path Asobu writes to. Instances are isolated; everything downloadable is shared
/// in one cache so twenty instances of 1.21.11 cost one copy of the assets, not twenty.
/// </summary>
public sealed class AsobuPaths(string root)
{
    public string Root { get; } = root;

    public string Instances => Path.Combine(Root, "instances");
    public string Cache => Path.Combine(Root, "cache");
    public string Versions => Path.Combine(Cache, "versions");
    public string Libraries => Path.Combine(Cache, "libraries");
    public string Assets => Path.Combine(Cache, "assets");
    public string AssetObjects => Path.Combine(Assets, "objects");
    public string AssetIndexes => Path.Combine(Assets, "indexes");
    public string AssetsVirtual => Path.Combine(Assets, "virtual");
    public string Java => Path.Combine(Root, "java");
    public string Logs => Path.Combine(Root, "logs");
    public string LogConfigs => Path.Combine(Cache, "log_configs");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string AccountsFile => Path.Combine(Root, "accounts.json");

    public string VersionDir(string id) => Path.Combine(Versions, id);
    public string VersionJsonFile(string id) => Path.Combine(VersionDir(id), id + ".json");
    public string VersionJarFile(string id) => Path.Combine(VersionDir(id), id + ".jar");
    public string NativesDir(string id) => Path.Combine(VersionDir(id), "natives");
    public string InstanceDir(string id) => Path.Combine(Instances, id);
    public string InstanceGameDir(string id) => Path.Combine(InstanceDir(id), "minecraft");

    /// <summary>
    /// Portable mode: drop a file named "portable" next to the executable and everything
    /// lives in .\data instead of AppData, so the whole launcher travels on a USB drive.
    /// </summary>
    public static AsobuPaths Resolve()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable")))
            return new AsobuPaths(Path.Combine(exeDir, "data"));

        return new AsobuPaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Asobu"));
    }

    public void EnsureCreated()
    {
        foreach (var directory in new[] { Root, Instances, Versions, Libraries, AssetObjects, AssetIndexes, Java, Logs, LogConfigs })
            Directory.CreateDirectory(directory);
    }
}
