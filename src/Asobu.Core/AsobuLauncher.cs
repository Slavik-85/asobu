using System.Diagnostics;
using Asobu.Core.Accounts;
using Asobu.Core.Instances;
using Asobu.Core.Java;
using Asobu.Core.Launch;
using Asobu.Core.Minecraft;

namespace Asobu.Core;

/// <summary>
/// The one object the UI talks to. Install, pick a Java runtime, build the command line, start
/// the game — in that order, because each step needs the one before it.
/// </summary>
public sealed class AsobuLauncher
{
    private readonly MinecraftInstaller _installer;
    private readonly JavaManager _java;
    private readonly LaunchBuilder _launchBuilder;
    private readonly GameLauncher _gameLauncher;

    public AsobuLauncher(HttpClient http, AsobuPaths? paths = null)
    {
        Paths = paths ?? AsobuPaths.Resolve();
        Paths.EnsureCreated();

        Meta = new MojangMeta(http);
        Instances = new InstanceStore(Paths);
        Accounts = new AccountStore(Paths);
        Settings = LauncherSettings.Load(Paths);

        _installer = new MinecraftInstaller(http, Paths, Meta);
        _java = new JavaManager(http, Paths);
        _launchBuilder = new LaunchBuilder(Paths, _installer);
        _gameLauncher = new GameLauncher(Paths);

        Microsoft = new MicrosoftAuth(http, Paths, Settings.MicrosoftClientId ?? "");
    }

    public AsobuPaths Paths { get; }
    public MojangMeta Meta { get; }
    public InstanceStore Instances { get; }
    public AccountStore Accounts { get; }
    public MicrosoftAuth Microsoft { get; }
    public LauncherSettings Settings { get; set; }

    /// <summary>Downloads everything an instance needs without starting the game.</summary>
    public Task<VersionJson> InstallAsync(
        Instance instance,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _installer.InstallAsync(instance.MinecraftVersion, progress, cancellationToken);

    public async Task<Process> LaunchAsync(
        Instance instance,
        MinecraftSession session,
        IProgress<InstallProgress>? progress = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var version = await InstallAsync(instance, progress, cancellationToken).ConfigureAwait(false);

        var javaExecutable = Settings.UsesManagedJava
            ? await _java.EnsureRuntimeAsync(version.JavaVersion, progress, cancellationToken).ConfigureAwait(false)
            : Settings.JavaSelection;

        if (!File.Exists(javaExecutable))
            throw new FileNotFoundException($"No Java executable at '{javaExecutable}'. Check Settings.", javaExecutable);

        // Windows keys the GPU choice off the executable path, so this has to happen after we
        // know which java binary is being launched.
        GpuPreferences.Apply(javaExecutable, Settings.Gpu);

        progress?.Report(new InstallProgress("Starting Minecraft", 1));
        var plan = _launchBuilder.Build(version, instance, Settings, session, javaExecutable);
        var process = _gameLauncher.Start(plan, instance, onOutput);

        instance.LastPlayed = DateTimeOffset.UtcNow;
        Instances.Save(instance);

        return process;
    }

    /// <summary>Turns an account into a live session, refreshing Microsoft tokens as needed.</summary>
    public Task<MinecraftSession> ResolveSessionAsync(Account account, CancellationToken cancellationToken = default) =>
        account.Kind == AccountKind.Microsoft
            ? Microsoft.GetSessionAsync(account, cancellationToken)
            : Task.FromResult(MinecraftSession.ForOffline(account));

    public IReadOnlyList<JavaInstallation> DetectSystemJava() => JavaManager.DetectSystemJava();

    public void SaveSettings()
    {
        Settings.Save(Paths);
    }

    /// <summary>Opens a folder in the system file manager.</summary>
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
}
