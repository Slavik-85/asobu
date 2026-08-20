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

    /// <summary>Loader installer jars, kept so reinstalling an instance costs no download.</summary>
    public string Installers => Path.Combine(Cache, "installers");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string AccountsFile => Path.Combine(Root, "accounts.json");

    public string VersionDir(string id) => Path.Combine(Versions, id);
    public string VersionJsonFile(string id) => Path.Combine(VersionDir(id), id + ".json");
    public string VersionJarFile(string id) => Path.Combine(VersionDir(id), id + ".jar");
    public string NativesDir(string id) => Path.Combine(VersionDir(id), "natives");
    /// <summary>
    /// One instance's folder. Keyed by the folder's own name — which follows the instance's —
    /// rather than by its id: see <see cref="Asobu.Core.Instances.Instance.Folder"/>.
    /// </summary>
    public string InstanceDir(string folder) => Path.Combine(Instances, folder);
    public string InstanceGameDir(string folder) => Path.Combine(InstanceDir(folder), "minecraft");

    /// <summary>
    /// Portable mode: drop a file named "portable" next to the executable and everything
    /// lives in .\data instead of AppData, so the whole launcher travels on a USB drive.
    /// </summary>
    public static AsobuPaths Resolve()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable")))
            return new AsobuPaths(Path.Combine(exeDir, "data"));

        return ResolveIn(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }

    /// <summary>
    /// Where the data lives inside a given roaming folder, and the move from the old name if
    /// one is due.
    ///
    /// Takes the folder rather than asking the OS for it so this can be exercised against a
    /// scratch directory. Environment.GetFolderPath reads the shell's own path and ignores the
    /// APPDATA variable, so a test that sets that variable tests nothing and runs against the
    /// real profile — which is how one nearly moved a real install.
    /// </summary>
    public static AsobuPaths ResolveIn(string roaming)
    {
        var root = Path.Combine(roaming, ".asobu");

        // What earlier builds used. The dot matches the convention every Minecraft launcher
        // follows — .minecraft sits in the same folder — and hides it from a casual look
        // through AppData, which is where people go hunting when something is wrong.
        var legacy = Path.Combine(roaming, "Asobu");

        if (!Directory.Exists(root) && Directory.Exists(legacy))
        {
            try
            {
                // A rename on the same volume, so instances and worlds are not copied about.
                Directory.Move(legacy, root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Something in there is open — a running game holding a world, most likely.
                // Carry on with the old folder rather than starting empty beside it and
                // leaving someone to wonder where their instances went.
                return new AsobuPaths(legacy);
            }
        }

        return new AsobuPaths(root);
    }

    public void EnsureCreated()
    {
        foreach (var directory in new[] { Root, Instances, Versions, Libraries, AssetObjects, AssetIndexes, Java, Logs, LogConfigs })
            Directory.CreateDirectory(directory);
    }
}
