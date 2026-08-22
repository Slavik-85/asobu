using System.IO.Compression;
using System.Diagnostics;
using Asobu.Core.Accounts;
using Asobu.Core.Hosting;
using Asobu.Core.Diagnostics;
using Asobu.Core.Instances;
using Asobu.Core.Java;
using Asobu.Core.Launch;
using Asobu.Core.Minecraft;
using Asobu.Core.Mods;
using Asobu.Core.Online;
using Asobu.Core.Download;

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
    private readonly Downloader _downloader;
    private readonly ForgeInstaller _forge;

    public AsobuLauncher(HttpClient http, AsobuPaths? paths = null)
    {
        Http = http;
        Paths = paths ?? AsobuPaths.Resolve();
        Paths.EnsureCreated();

        // Set before anything scans a mods folder: without it every scan reopens every jar.
        ModScanner.Cache = new ModMetadataCache(Paths);

        Web = new WebCache(http);

        // Off the startup path entirely: listing a folder of a few thousand pictures is not
        // something to do before the window exists, and nothing waits on the result.
        _ = Task.Run(Web.Prune);

        Meta = new MojangMeta(http);
        Instances = new InstanceStore(Paths);
        Accounts = new AccountStore(Paths);
        Settings = LauncherSettings.Load(Paths);

        // Someone who already has an account or an instance is not seeing Asobu for the first
        // time, whatever the flag says — the flag simply did not exist when they installed.
        // Checked only while it is unset, so it costs a returning user one read, once ever.
        if (!Settings.IntroCompleted && (Accounts.Load().Count > 0 || Instances.LoadAll().Count > 0))
        {
            Settings.IntroCompleted = true;
            Settings.TourOffered = true;
            Settings.Save(Paths);
        }

        _installer = new MinecraftInstaller(http, Paths, Meta);
        _java = new JavaManager(http, Paths);
        _launchBuilder = new LaunchBuilder(Paths, _installer);
        _gameLauncher = new GameLauncher(Paths);

        Fabric = new FabricStyleMeta(http, LoaderFlavour.Fabric);
        Quilt = new FabricStyleMeta(http, LoaderFlavour.Quilt);
        Loaders = new LoaderCatalog(http);
        Modrinth = new Modrinth(http);
        // A key pasted into Settings wins over the one compiled in, so someone can use their
        // own without rebuilding.
        CurseForge = new CurseForge(http, () => Settings.CurseForgeApiKey ?? BuildConfig.CurseForgeApiKey);
        ModSources = [Modrinth, CurseForge];
        Mods = new ModCatalogue(Modrinth, CurseForge);
        OptiFine = new OptiFine(http);
        _downloader = new Downloader(http);
        _forge = new ForgeInstaller(Paths, _downloader);

        var xbox = new XboxChain(http);
        Microsoft = new MicrosoftAuth(xbox, Paths, Settings.MicrosoftClientId ?? "");
        DeviceCode = new DeviceCodeAuth(http, new TokenVault(Paths), xbox);

        Session = new SessionShim(http);

        Friends = new FriendsClient(http, Paths);
        Shares = new ShareClient(http, Paths, Friends);

        // After sharing, which it needs: a code someone pastes is one of the things it imports.
        Importer = new InstanceImporter(http, Paths, Instances, Modrinth, CurseForge, Mods, Shares);
    }

    /// <summary>Shared client, already carrying Asobu's user agent.</summary>
    public HttpClient Http { get; }

    /// <summary>
    /// Which port the running game has opened to LAN, read from its own output. Lives here
    /// because the page that starts the game and the page that hosts it are different ones.
    /// </summary>
    public LanPortWatch LanPorts { get; } = new();

    /// <summary>
    /// The instance the game is playing, or null. Kept so the friends page can say what a world
    /// is made of without having to guess which of several instances opened it.
    /// </summary>
    public Instance? Running { get; private set; }

    /// <summary>Games started by this launcher and still alive.</summary>
    private readonly List<Process> _games = [];

    /// <summary>
    /// Asobu's stand-in for Mojang's session server, so an invited friend without a Microsoft
    /// account can be let into a world. Started on the first launch and left running; it forwards
    /// everything it does not answer itself, so a real account is unaffected by its presence.
    /// </summary>
    public SessionShim Session { get; }

    /// <summary>
    /// Pictures kept on disk between runs. Every screen that shows artwork goes through this
    /// rather than the client directly, so a second launch draws from the cache instead of
    /// fetching a hundred logos again.
    /// </summary>
    public WebCache Web { get; }

    public AsobuPaths Paths { get; }
    public MojangMeta Meta { get; }
    public FabricStyleMeta Fabric { get; }

    /// <summary>Quilt's own metadata service, which answers in the same shape Fabric's does.</summary>
    public FabricStyleMeta Quilt { get; }
    public LoaderCatalog Loaders { get; }
    public Modrinth Modrinth { get; }
    public CurseForge CurseForge { get; }

    /// <summary>Every place mods can come from, in the order a download is attempted.</summary>
    public IReadOnlyList<IModSource> ModSources { get; }

    /// <summary>Both providers as one catalogue, which is how the browser asks.</summary>
    public ModCatalogue Mods { get; }

    /// <summary>
    /// OptiFine, which lives on its own website and nowhere else. Only reached where Embeddium
    /// has nothing — the old Forge versions where it is the only thing that does the job.
    /// </summary>
    public OptiFine OptiFine { get; }
    public InstanceStore Instances { get; }

    /// <summary>Makes instances out of pack files, other launchers' folders, and shared codes.</summary>
    public InstanceImporter Importer { get; }
    public AccountStore Accounts { get; }
    public MicrosoftAuth Microsoft { get; }

    /// <summary>Sign-in that needs no app registration of ours. See DeviceCodeAuth for the cost.</summary>
    public DeviceCodeAuth DeviceCode { get; }

    /// <summary>The Asobu network: who your friends are and whether they're around.</summary>
    public FriendsClient Friends { get; }

    /// <summary>Instances passed around as a code, which lasts a week.</summary>
    public ShareClient Shares { get; }
    public LauncherSettings Settings { get; set; }

    /// <summary>
    /// Downloads everything an instance needs without starting the game.
    ///
    /// Vanilla is installed first even for a modded instance: Forge and NeoForge build their
    /// patched client *from* the vanilla jar, so it has to exist before their processors run.
    /// </summary>
    public async Task<VersionJson> InstallAsync(
        Instance instance,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new InstallProgress("Reading version metadata", 0));

        var vanilla = await Meta.GetResolvedVersionAsync(instance.MinecraftVersion, cancellationToken)
            .ConfigureAwait(false);

        await _installer.InstallAsync(vanilla, progress, cancellationToken).ConfigureAwait(false);

        var version = instance.IsModded
            ? await InstallLoaderAsync(instance, vanilla, progress, cancellationToken).ConfigureAwait(false)
            : vanilla;

        await EnsureModsAsync(instance, progress, cancellationToken).ConfigureAwait(false);

        return version;
    }

    /// <summary>
    /// A project's picture, for the mods whose jars carry none of their own.
    ///
    /// Through the same cache every screen uses, so a mod already seen while browsing costs
    /// nothing to keep. Failure is silence: an install must not turn on whether a logo arrived.
    /// </summary>
    private async Task<byte[]?> ArtworkAsync(string? url, CancellationToken cancellationToken)
    {
        if (url is not { Length: > 0 }) return null;

        try
        {
            return await Web.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches OptiFine, which takes two requests and has no checksum to check against.
    ///
    /// No hash and no size, because the site publishes neither — so unlike every other download
    /// here this one is taken on trust that it came from optifine.net over TLS. Worth knowing,
    /// and worth being the only one: it is the reason this is reached for last rather than first.
    /// </summary>
    private async Task EnsureOptiFineAsync(
        Instance instance,
        string directory,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var build = await OptiFine.GetLatestAsync(instance.MinecraftVersion, cancellationToken)
            .ConfigureAwait(false);

        // No build for this version is a fact about OptiFine, not a launch failure — the same
        // answer the Modrinth path gives when a mod has nothing.
        if (build is null) return;

        var url = await OptiFine.GetDownloadUrlAsync(build, cancellationToken).ConfigureAwait(false);
        if (url is null) return;

        progress?.Report(new InstallProgress($"Installing {build.FileName}", 0));

        await _downloader.RunAsync(
            [new DownloadTask(url, Path.Combine(directory, build.FileName))],
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every loader ends up as a version document inheriting from the vanilla one, which the
    /// resolver flattens exactly as it would any other chain. Only how that document is obtained
    /// differs: Fabric publishes it, Forge and NeoForge have to build it first.
    /// </summary>
    private async Task<VersionJson> InstallLoaderAsync(
        Instance instance,
        VersionJson vanilla,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (instance.LoaderVersion is not { Length: > 0 } loaderVersion)
            throw new InvalidOperationException($"{instance.Name} has no {instance.Loader} version recorded.");

        VersionJson document;

        if (instance.UsesFabricFamily)
        {
            // The one place the two differ: which service is asked. Both hand back a profile
            // that inherits from the vanilla version, which the resolver flattens either way.
            document = await (instance.UsesQuilt ? Quilt : Fabric)
                .GetProfileAsync(instance.MinecraftVersion, loaderVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Forge's build steps are themselves Java programs, so a runtime is needed here,
            // before the launch would otherwise have asked for one.
            var java = await ResolveJavaAsync(instance, vanilla, progress, cancellationToken).ConfigureAwait(false);

            var url = instance.Loader.Equals(Minecraft.Loaders.NeoForge, StringComparison.OrdinalIgnoreCase)
                ? LoaderCatalog.NeoForgeInstallerUrl(loaderVersion)
                : await Loaders.ForgeInstallerUrlAsync(instance.MinecraftVersion, loaderVersion, cancellationToken)
                    .ConfigureAwait(false);

            document = await _forge
                .EnsureAsync(url, java, Paths.VersionJarFile(vanilla.Id), progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var merged = await VersionResolver.ResolveAsync(
            document.Id,
            (wanted, token) => wanted == document.Id
                ? Task.FromResult(document)
                : Meta.GetVersionAsync(wanted, token),
            cancellationToken).ConfigureAwait(false);

        // A second pass, this time fetching the loader's own libraries.
        return await _installer.InstallAsync(merged, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Which java to use, honouring the instance's own choice before the launcher's.</summary>
    private async Task<string> ResolveJavaAsync(
        Instance instance,
        VersionJson version,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var settings = Settings.ForInstance(instance, Paths);

        return settings.UsesManagedJava
            ? await _java.EnsureRuntimeAsync(version.JavaVersion, progress, cancellationToken).ConfigureAwait(false)
            : settings.JavaSelection;
    }

    /// <summary>
    /// Keeps the chosen performance mod present. Anything already sitting in mods/ under that
    /// name is left alone, disabled ones included — re-adding a mod someone deliberately turned
    /// off would be worse than not having it.
    /// </summary>
    /// <summary>
    /// Fetches the performance mod an instance was created wanting, on its own.
    ///
    /// The same work a launch does, reachable without launching: somebody who ticks the box while
    /// making an instance means now, not the first time they press Play. Safe to call twice — it
    /// looks in the folder first and does nothing when the mod is already there.
    /// </summary>
    public Task InstallPerformanceModAsync(
        Instance instance,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        EnsureModsAsync(instance, progress, cancellationToken);

    private async Task EnsureModsAsync(
        Instance instance, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (instance.PerformanceMod is not { Length: > 0 } project) return;

        var directory = ModScanner.ModsDirectory(Paths, instance.Folder);
        Directory.CreateDirectory(directory);

        if (Directory.EnumerateFiles(directory)
            .Any(f => Path.GetFileName(f).StartsWith(project, StringComparison.OrdinalIgnoreCase)))
            return;

        // Fully qualified: the property below is called OptiFine too, and the type is what carries
        // the constant.
        if (project.Equals(global::Asobu.Core.Mods.OptiFine.Marker, StringComparison.OrdinalIgnoreCase))
        {
            await EnsureOptiFineAsync(instance, directory, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var file = await Modrinth
            .GetLatestAsync(project, instance.MinecraftVersion, instance.Loader, cancellationToken)
            .ConfigureAwait(false);

        // No build for this version and loader is a fact about the mod, not a launch failure.
        if (file is null) return;

        progress?.Report(new InstallProgress($"Installing {file.FileName}", 0));

        await _downloader.RunAsync(
            [new DownloadTask(file.Url, Path.Combine(directory, file.FileName), file.Sha1, file.Size)],
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }


    public async Task<Process> LaunchAsync(
        Instance instance,
        MinecraftSession session,
        IProgress<InstallProgress>? progress = null,
        Action<string>? onOutput = null,
        string? joinServer = null,
        CancellationToken cancellationToken = default)
    {
        var version = await InstallAsync(instance, progress, cancellationToken).ConfigureAwait(false);

        // The instance's own memory, runtime and JVM arguments win over the launcher defaults,
        // so one heavy modpack can have 8 GB and its own Java without moving everything else.
        var settings = Settings.ForInstance(instance, Paths);

        var javaExecutable = settings.UsesManagedJava
            ? await _java.EnsureRuntimeAsync(version.JavaVersion, progress, cancellationToken).ConfigureAwait(false)
            : settings.JavaSelection;

        if (!File.Exists(javaExecutable))
            throw new FileNotFoundException($"No Java executable at '{javaExecutable}'. Check Settings.", javaExecutable);

        // Windows keys the GPU choice off the executable path, so this has to happen after we
        // know which java binary is being launched.
        GpuPreferences.Apply(javaExecutable, settings.Gpu);

        // An offline account has nobody to vouch for it, and a world opened to LAN always asks —
        // so the stand-in answers on its behalf. "legacy" is what an offline session carries; a
        // Microsoft one says "msa" and still goes to Mojang for everything.
        Session.JoinsWithoutMojang = session.UserType == "legacy";

        var sessionHosts = Session.TryStart()
            ? new SessionUpstreams(Session.AuthHost, Session.AccountHost, Session.SessionHost, Session.ServicesHost)
            : null;

        progress?.Report(new InstallProgress("Starting Minecraft", 1));
        var plan = _launchBuilder.Build(
            version, instance, settings, session, javaExecutable, joinServer, sessionHosts);

        // Every line the game prints passes the port watch on its way to whoever asked for it.
        // Hooked here rather than at the call sites so that opening a world to LAN is noticed
        // however the game was started.
        var process = _gameLauncher.Start(plan, instance, line =>
        {
            LanPorts.Note(line);
            onOutput?.Invoke(line);
        });

        Running = instance;
        lock (_games) _games.Add(process);

        process.Exited += (_, _) =>
        {
            LanPorts.Forget();
            Running = null;
            lock (_games) _games.Remove(process);
        };

        instance.LastPlayed = DateTimeOffset.UtcNow;
        Instances.Save(instance);

        return process;
    }

    /// <summary>
    /// Closes any game this launcher started, on the way out.
    ///
    /// Minecraft normally outlives its launcher, and for most launchers that is the right way
    /// round. Not for this one: the tunnel a friend is connected through, the door in front of the
    /// world, and the stand-in that vouched for whoever is standing in it all live in this process.
    /// A game left running once this closes is one whose multiplayer has quietly stopped working
    /// and whose owner has no way of knowing.
    ///
    /// Asked to close rather than killed. Closing the window is how Minecraft is meant to be told
    /// to stop, and it saves the world on the way; killing it outright would throw away whatever
    /// had happened since the last autosave. The kill is only for a game that will not go.
    /// </summary>
    public void StopGames(TimeSpan within)
    {
        Process[] games;
        lock (_games) games = [.. _games];

        foreach (var game in games)
        {
            try { if (!game.HasExited) game.CloseMainWindow(); }
            catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
            {
                // No window to close — a headless or already-gone process. The wait below sorts it.
            }
        }

        var deadline = DateTime.UtcNow + within;
        foreach (var game in games)
        {
            try
            {
                var left = deadline - DateTime.UtcNow;
                if (left > TimeSpan.Zero) game.WaitForExit(left);

                if (!game.HasExited) game.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or SystemException)
            {
                // Already gone, or not ours to kill. Either way there is nothing left to do.
            }
        }
    }

    /// <summary>
    /// Turns an account into a live session, refreshing Microsoft tokens as needed. The route is
    /// taken from the account rather than from settings, so accounts added one way keep working
    /// after the launcher's default sign-in is switched to the other.
    /// </summary>
    public Task<MinecraftSession> ResolveSessionAsync(Account account, CancellationToken cancellationToken = default)
    {
        if (account.Kind != AccountKind.Microsoft)
            return Task.FromResult(MinecraftSession.ForOffline(account));

        return account.Method == AuthMethod.Registered
            ? Microsoft.GetSessionAsync(account, cancellationToken)
            : DeviceCode.GetSessionAsync(account, cancellationToken);
    }

    /// <summary>Forgets whatever the account's sign-in route cached for it.</summary>
    public async Task SignOutAsync(Account account)
    {
        if (account.Kind != AccountKind.Microsoft) return;

        if (account.Method == AuthMethod.Registered) await Microsoft.SignOutAsync(account).ConfigureAwait(false);
        else DeviceCode.SignOut(account);
    }

    public IReadOnlyList<JavaInstallation> DetectSystemJava() => JavaManager.DetectSystemJava();

    public void SaveSettings()
    {
        Settings.Save(Paths);
    }

    /// <summary>
    /// Puts a mod into an instance's mods folder, trying each provider that carries it in turn.
    /// CurseForge goes first and Modrinth picks up what it will not hand over: a CurseForge
    /// author can forbid third-party downloads, and the API then returns a file with no URL.
    /// Reconstructing the CDN address from the file id would work and is exactly the thing that
    /// flag exists to prevent — so the same mod is fetched from the other shop instead.
    /// </summary>
    public async Task<ModInstallResult> InstallModAsync(
        Instance instance, CatalogueMod entry, CancellationToken cancellationToken = default)
    {
        if (ModScanner.ContentDirectory(Paths, instance.Folder, entry.Kind) is not { } destination)
            return new ModInstallResult(null, null, Reason: RefusalFor(entry.Kind));

        // What is already there, looked up before anything is fetched. Installing a mod the
        // instance has is a replacement rather than a second copy — which the loader refuses to
        // start with anyway — so the old file is taken out once the new one has landed.
        var previous = InstalledMods.For(Paths, instance).Find(entry);

        var blocked = false;

        foreach (var listing in entry.DownloadOrder)
        {
            var source = ModSources.FirstOrDefault(s => s.Provider == listing.Provider);
            if (source is null) continue;

            var download = await source
                .GetDownloadAsync(
                    listing.Id, instance.MinecraftVersion, instance.Loader, entry.Kind, cancellationToken)
                .ConfigureAwait(false);

            // No build for this version and loader; the next provider may still have one.
            if (download is null) continue;

            if (download.Url is not { Length: > 0 } url)
            {
                blocked = true;
                continue;
            }

            Directory.CreateDirectory(destination);

            var landed = Path.Combine(destination, download.FileName);

            await _downloader.RunAsync(
                [new DownloadTask(url, landed, download.Sha1, download.Size)],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // A world is a folder to the game, so the zip is unpacked and then thrown away.
            if (entry.Kind == ModKind.World)
                return UnpackAndTidy(landed, destination, download.FileName) is { } world
                    ? new ModInstallResult(world, listing.Provider)
                    : new ModInstallResult(null, null, Reason: "That world's archive had no level.dat in it.");

            RetirePreviousCopy(previous, landed, entry.Kind);

            // What the shop called it, kept beside the instance. Some jars carry no name of their
            // own — Essential's Forge build has no manifest at all — and this is the only moment
            // the launcher will ever know what the project is actually called.
            ModCredits.Record(Paths, instance, download.FileName,
                new ModCredit(listing.Title, listing.Author, listing.ProviderName),
                await ArtworkAsync(listing.IconUrl, cancellationToken).ConfigureAwait(false));

            // Dependencies go to mods/ whatever needed them: what a shader pack or a resource
            // pack cannot run without is a loader mod — Iris, or the mod whose blocks it retextures.
            var carried = await CarryDependenciesAsync(
                source, instance, ModScanner.ModsDirectory(Paths, instance.Folder),
                download.Requires, cancellationToken).ConfigureAwait(false);

            return new ModInstallResult(download.FileName, listing.Provider, Dependencies: carried);
        }

        return new ModInstallResult(null, null, blocked);
    }

    /// <summary>
    /// Puts one particular build into an instance, rather than whichever the provider considers
    /// current. Picked off a mod's version list, so the file is already known and there is
    /// nothing left to ask for — except what it depends on, which is looked up like any other.
    /// </summary>
    /// <param name="project">
    /// The catalogue entry this build belongs to, where the caller knows it. Only used to find
    /// the copy already installed so it can be replaced rather than added beside — a version
    /// list gives no way to work out which file on disk is an older build of the same mod.
    /// </param>
    public async Task<ModInstallResult> InstallVersionAsync(
        Instance instance, ModVersion version, ModKind kind = ModKind.Mod,
        CatalogueMod? project = null,
        CancellationToken cancellationToken = default)
    {
        if (ModScanner.ContentDirectory(Paths, instance.Folder, kind) is not { } directory)
            return new ModInstallResult(null, null, Reason: RefusalFor(kind));

        if (version.Url is not { Length: > 0 } url) return new ModInstallResult(null, null, Blocked: true);

        var previous = project is null ? null : InstalledMods.For(Paths, instance).Find(project);

        Directory.CreateDirectory(directory);

        var landed = Path.Combine(directory, version.FileName);

        await _downloader.RunAsync(
            [new DownloadTask(url, landed, version.Sha1, version.Size)],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (kind == ModKind.World)
            return UnpackAndTidy(landed, directory, version.FileName) is { } world
                ? new ModInstallResult(world, version.Provider)
                : new ModInstallResult(null, null, Reason: "That world's archive had no level.dat in it.");

        RetirePreviousCopy(previous, landed, kind);

        if (project is { } listed)
            ModCredits.Record(Paths, instance, version.FileName,
                new ModCredit(listed.Title, listed.Author, version.Provider.ToString()),
                await ArtworkAsync((listed.Modrinth ?? listed.CurseForge)?.IconUrl, cancellationToken)
                    .ConfigureAwait(false));

        var source = ModSources.FirstOrDefault(s => s.Provider == version.Provider);

        var carried = source is null
            ? []
            : await CarryDependenciesAsync(
                source, instance, ModScanner.ModsDirectory(Paths, instance.Folder),
                version.Requires, cancellationToken).ConfigureAwait(false);

        return new ModInstallResult(version.FileName, version.Provider, Dependencies: carried);
    }

    /// <summary>
    /// Takes out the build that was already there, now that its replacement has landed.
    ///
    /// Two builds of one mod in a folder is exactly what a loader refuses to start with, so
    /// installing a different version has to mean replacing rather than adding beside. Done after
    /// the download rather than before, so a download that fails leaves the working copy alone.
    /// </summary>
    private static void RetirePreviousCopy(ModEntry? previous, string landed, ModKind kind)
    {
        // A world is a folder full of somebody's building, and "you already have this world" is
        // never a reason to delete it. Installing one again lands beside it.
        if (previous is null || kind == ModKind.World) return;

        // Downloaded over itself: the same build, same file name. Nothing to retire, and deleting
        // it here would delete what was just fetched.
        if (string.Equals(previous.Path, landed, StringComparison.OrdinalIgnoreCase)) return;

        // Only ever a file in the folder being installed into. The copy is looked up across all
        // of an instance's content folders, so a project that ships both a mod and a resource
        // pack under one name can match the jar in mods/ while a pack is being put into
        // resourcepacks/ — and retiring that would delete a mod nobody touched.
        if (!string.Equals(
                Path.GetDirectoryName(previous.Path), Path.GetDirectoryName(landed),
                StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            File.Delete(previous.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Held open or read-only. Two builds is a mess the loader will complain about, but
            // the one that was asked for is in — which beats failing the install over tidying.
        }

        // Switched off before, switched off after. An update is a newer copy of a mod, not a
        // decision to start running it: somebody who turned a mod off to stop it crashing their
        // game, and then updated everything, was having it quietly turned back on for them.
        if (!previous.Enabled) SwitchOff(landed);
    }

    /// <summary>
    /// Renames a freshly downloaded file so the game ignores it, the way the copy it replaced was
    /// being ignored.
    ///
    /// Failing here leaves the mod on rather than failing the update, which is the same trade the
    /// deletion above makes: the build somebody asked for is installed either way, and a mod that
    /// came back on is a switch to flick rather than a download to do again.
    /// </summary>
    private static void SwitchOff(string landed)
    {
        var off = landed + ModScanner.DisabledSuffix;

        try
        {
            // A leftover from an earlier build of the same name, which would otherwise make this
            // rename fail and leave the mod switched on.
            if (File.Exists(off)) File.Delete(off);

            File.Move(landed, off);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Why a kind of content cannot simply be dropped into an instance. A real thing people will
    /// click Add on, and saying so beats a file landing where nothing reads it.
    /// </summary>
    private static string RefusalFor(ModKind kind) => kind switch
    {
        ModKind.Modpack => "A modpack becomes an instance of its own rather than going into one.",
        _ => "Asobu doesn't know where this kind of content belongs.",
    };

    /// <summary>
    /// What came of adding files by hand. <paramref name="Skipped"/> names anything that was not
    /// put in and why — a mod dropped into resource packs is a mistake worth saying out loud
    /// rather than a file that quietly never appears.
    /// </summary>
    public sealed record LocalAddResult(IReadOnlyList<string> Added, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Copies files the person already has into the instance. Copied, never moved: what is in
    /// someone's Downloads folder is theirs, and an "add" that made the original disappear would
    /// be a surprise nobody asked for.
    ///
    /// Worlds are the exception in shape only — a zip is unpacked and a folder copied — and both
    /// are marked as Asobu's so they show in the instance's world list.
    /// </summary>
    public LocalAddResult AddLocalContent(
        Instance instance, ModKind kind, IReadOnlyList<string> paths)
    {
        var added = new List<string>();
        var skipped = new List<string>();

        // Everything is sorted per file below, so it needs no folder of its own.
        var destination = kind == ModKind.Any
            ? Paths.InstanceGameDir(instance.Folder)
            : ModScanner.ContentDirectory(Paths, instance.Folder, kind);

        if (destination is null) return new LocalAddResult(added, [.. paths.Select(Path.GetFileName)!]);

        Directory.CreateDirectory(destination);

        var wanted = kind == ModKind.Mod ? ".jar" : ".zip";

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            try
            {
                // Adding while the list shows Everything: nobody said what this is, so it is read
                // rather than guessed, and each file goes wherever its own contents belong.
                if (kind == ModKind.Any)
                {
                    var sniffed = ModScanner.SniffKind(path);

                    if (sniffed == ModKind.Any)
                    {
                        skipped.Add($"{name} — Asobu can't tell what this is");
                        continue;
                    }

                    var one = AddLocalContent(instance, sniffed, [path]);
                    added.AddRange(one.Added);
                    skipped.AddRange(one.Skipped);
                    continue;
                }

                if (kind == ModKind.World)
                {
                    if (AddLocalWorld(path, destination, name) is { } world) added.Add(world);
                    else skipped.Add($"{name} — not a world folder or archive");

                    continue;
                }

                // A pack kept unzipped is a folder, and the game reads it either way.
                if (Directory.Exists(path))
                {
                    if (kind == ModKind.Mod)
                    {
                        skipped.Add($"{name} — a mod has to be a .jar");
                        continue;
                    }

                    CopyTree(path, Unique(Path.Combine(destination, name)));
                    added.Add(name);
                    continue;
                }

                if (!File.Exists(path))
                {
                    skipped.Add($"{name} — no longer there");
                    continue;
                }

                if (!name.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add($"{name} — expected a {wanted}");
                    continue;
                }

                var target = Unique(Path.Combine(destination, name));
                File.Copy(path, target, overwrite: false);
                added.Add(Path.GetFileName(target));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{name} — {e.Message}");
            }
        }

        return new LocalAddResult(added, skipped);
    }

    /// <summary>A world from disk: an archive to unpack, or a folder to copy.</summary>
    private static string? AddLocalWorld(string path, string savesDirectory, string name)
    {
        if (Directory.Exists(path))
        {
            if (!File.Exists(Path.Combine(path, "level.dat"))) return null;

            var target = Unique(Path.Combine(savesDirectory, name));
            CopyTree(path, target);
            ModScanner.MarkWorld(target, name);

            return Path.GetFileName(target);
        }

        return File.Exists(path) ? UnpackWorld(path, savesDirectory, name) : null;
    }

    /// <summary>A path nothing is using yet, so adding never writes over what is already there.</summary>
    private static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid().ToString("n")[..6]}){extension}");
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    /// <summary>Unpacks the world and removes the archive, which the game has no use for.</summary>
    private static string? UnpackAndTidy(string archivePath, string savesDirectory, string fallbackName)
    {
        try
        {
            return UnpackWorld(archivePath, savesDirectory, fallbackName);
        }
        finally
        {
            try
            {
                File.Delete(archivePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover zip in saves/ is untidy, not broken.
            }
        }
    }

    /// <summary>
    /// Unpacks a downloaded world into saves/ and marks it as one Asobu installed.
    ///
    /// Worlds are the one kind that cannot simply be dropped in: the game reads a folder, not a
    /// zip. The archive usually wraps everything in one folder of its own, which becomes the
    /// world; where it does not, the file name does.
    /// </summary>
    private static string? UnpackWorld(string archivePath, string savesDirectory, string fallbackName)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);

            // The wrapping folder every entry shares, if there is one.
            string? root = null;
            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                var slash = path.IndexOf('/');

                if (slash <= 0) { root = ""; break; }

                var top = path[..(slash + 1)];
                if (root is null) root = top;
                else if (!root.Equals(top, StringComparison.OrdinalIgnoreCase)) { root = ""; break; }
            }

            root ??= "";

            var name = root.Length > 0
                ? root.TrimEnd('/')
                : Path.GetFileNameWithoutExtension(fallbackName);

            var destination = Path.Combine(savesDirectory, name);

            // Never over the top of a world that is already there — including one of the
            // player's own that happens to share a name.
            for (var n = 2; Directory.Exists(destination) && n < 100; n++)
                destination = Path.Combine(savesDirectory, $"{name} ({n})");

            Directory.CreateDirectory(destination);

            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (path.EndsWith('/') || entry.Name.Length == 0) continue;

                var target = Path.GetFullPath(Path.Combine(destination, path[root.Length..]));

                // Refuse anything pointing outside the world it claims to be.
                if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            if (!File.Exists(Path.Combine(destination, "level.dat")))
            {
                Directory.Delete(destination, recursive: true);
                return null;
            }

            ModScanner.MarkWorld(destination, fallbackName);
            return Path.GetFileName(destination);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>How deep a chain of dependencies is followed before it is treated as a mistake.</summary>
    private const int DependencyDepth = 4;

    /// <summary>And how many are fetched in total, however the chain branches.</summary>
    private const int DependencyLimit = 16;

    /// <summary>
    /// Fetches what a mod will not run without, and what those need in turn. A mod that declares
    /// a dependency and arrives alone does not work, and the person who asked for it has no way
    /// of knowing which of the twenty things on the page was the missing one.
    ///
    /// Breadth-first from the same provider the mod came from: a Modrinth project id means
    /// nothing to CurseForge, and mixing the two would fetch the same library twice under two
    /// different file names.
    /// </summary>
    private async Task<IReadOnlyList<string>> CarryDependenciesAsync(
        IModSource source,
        Instance instance,
        string directory,
        IReadOnlyList<string> required,
        CancellationToken cancellationToken)
    {
        if (required.Count == 0) return [];

        var carried = new List<string>();
        var seen = new HashSet<string>(required, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Id, int Depth)>(required.Select(id => (id, 1)));

        // What the mods folder holds before any of this arrives. A dependency that is already
        // installed at an older build has to be replaced rather than joined, exactly as the mod
        // that asked for it would be — this is the same duplicate by another road, and the one
        // nobody clicked for.
        var already = ModScanner.Scan(directory);

        while (pending.Count > 0 && carried.Count < DependencyLimit)
        {
            var (id, depth) = pending.Dequeue();

            var download = await source
                .GetDownloadAsync(
                    id, instance.MinecraftVersion, instance.Loader, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // No build for this instance, or an author who forbids third-party downloads. Said
            // nowhere and thrown nowhere: the mod itself is in, and a missing dependency shows up
            // as a mod that does not load, which is what it already would have been.
            if (download?.Url is not { Length: > 0 } url) continue;

            var landed = Path.Combine(directory, download.FileName);

            await _downloader.RunAsync(
                [new DownloadTask(url, landed, download.Sha1, download.Size)],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // A dependency is only ever a mod, so the world guard never applies here — but it
            // costs nothing to go through the same door as everything else.
            RetirePreviousCopy(InstalledMods.OlderBuildOf(download.FileName, already), landed, ModKind.Mod);

            carried.Add(download.FileName);

            if (depth >= DependencyDepth) continue;

            foreach (var next in download.Requires)
                if (seen.Add(next))
                    pending.Enqueue((next, depth + 1));
        }

        return carried;
    }

    /// <summary>
    /// What a swap came to. <paramref name="Installed"/> is the build that went in; null means
    /// nothing suitable was found, and the reason says which of the several ways that happened.
    /// </summary>
    public sealed record ModSwapResult(string? Installed, string? Replaced, string? Reason)
    {
        public bool Swapped => Installed is { Length: > 0 };
    }

    /// <summary>
    /// Replaces an installed mod with a build that satisfies what another mod asked for.
    ///
    /// The jar on disk is identified by its hash rather than by the id in the log — a mod's id
    /// belongs to the loader, the catalogue has its own, and the two agree only by luck. From
    /// there it is an ordinary version list, filtered to what this instance runs and to what the
    /// complaint will accept, newest first.
    /// </summary>
    /// <param name="excluding">
    /// File names never to install, whatever the catalogue says about them. Builds already known
    /// to crash this instance go here: a shop's version tags are the author's claim about what a
    /// build runs on, and a crash is proof against it.
    /// </param>
    public async Task<ModSwapResult> SwapModAsync(
        Instance instance,
        ModConflict conflict,
        IReadOnlySet<string>? excluding = null,
        CancellationToken cancellationToken = default)
    {
        var first = await SwapOneAsync(
                instance, conflict.ModId, conflict.ModName, conflict.Wanted, excluding, cancellationToken)
            .ConfigureAwait(false);

        if (first.Swapped || conflict.Alternative is not { } other) return first;

        // Nothing fit for the mod the loader named, so try the disagreement from the other end.
        // Two mods that will not sit together can usually be fixed by moving either of them, and
        // the loader only ever suggests moving one — refusing here would leave an instance
        // unlaunchable over a fix that was available all along.
        var second = await SwapOneAsync(
                instance, other.ModId, other.ModName, other.Wanted, excluding, cancellationToken)
            .ConfigureAwait(false);

        return second.Swapped
            ? second
            // Both reasons, since "no build fits" about one mod does not explain why the other
            // was not used either.
            : new ModSwapResult(null, null, $"{first.Reason} {second.Reason}");
    }

    /// <summary>One end of a disagreement: find a build of this mod that fits, and install it.</summary>
    private async Task<ModSwapResult> SwapOneAsync(
        Instance instance,
        string modId,
        string modName,
        VersionBound wantedBound,
        IReadOnlySet<string>? excluding,
        CancellationToken cancellationToken)
    {
        var directory = ModScanner.ModsDirectory(Paths, instance.Folder);

        var installed = ModScanner.Scan(directory).FirstOrDefault(mod =>
            string.Equals(mod.ModId, modId, StringComparison.OrdinalIgnoreCase));

        if (installed is null) return new ModSwapResult(null, null, $"{modName} is not in this instance.");

        var (source, projectId) = await IdentifyAsync(installed.Path, cancellationToken).ConfigureAwait(false);

        if (source is null || projectId is null)
            return new ModSwapResult(null, null, $"Neither shop recognises the installed {modName}.");

        var versions = await source.GetVersionsAsync(projectId, cancellationToken).ConfigureAwait(false);

        var wanted = versions
            .Where(version => version.CanDownload)
            .Where(version => version.GameVersions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase))
            .Where(version => version.Loaders.Contains(instance.Loader, StringComparer.OrdinalIgnoreCase))
            .Where(version => wantedBound.Accepts(version.VersionNumber))

            // Never the build that is already there. It is in range and it is what the loader
            // just refused to start with, so offering it back is offering nothing — a bound like
            // "any 0.9.x" means "some 0.9.x that works", and this one demonstrably does not.
            .Where(version => !string.Equals(version.FileName, installed.FileName, StringComparison.OrdinalIgnoreCase))

            // And never one that has already crashed this instance. The shops say which game
            // versions a build supports, but that is the author's claim rather than a fact — a
            // build tagged for 1.21.8 that dies on 1.21.8 has settled the question, and without
            // this the search comes straight back to it.
            .Where(version => excluding is null || !excluding.Contains(version.FileName))

            // A release ahead of a prerelease, then newest first. Ordered by the published
            // version string rather than by date: a mod can publish a fix for an older branch
            // after a newer one, and the higher number is the one that was asked for.
            //
            // Stability leads because of how these disagreements usually arise: the installed
            // build is an alpha that moved ahead of what its neighbour supports, and the newest
            // alpha under it is likely to have the same problem. The last release is the build
            // most things were tested against.
            .OrderByDescending(version => version.Channel == ModChannel.Release)
            .ThenByDescending(version => version.VersionNumber, Comparer<string>.Create(VersionBound.Compare))
            .FirstOrDefault();

        if (wanted is null)
            return new ModSwapResult(null, null,
                $"No other build of {modName} for {instance.LoaderName} {instance.MinecraftVersion} fits.");

        await _downloader.RunAsync(
            [new DownloadTask(wanted.Url!, Path.Combine(directory, wanted.FileName), wanted.Sha1, wanted.Size)],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Only once the replacement is on disk. Deleting first would turn a failed download into
        // a missing mod, which is worse than the wrong version of one.
        try
        {
            File.Delete(installed.Path);
        }
        catch (IOException)
        {
            // Left behind rather than failed: two builds of one mod is a mess the loader will
            // complain about, but the new one is in and saying so is more use than an error.
            return new ModSwapResult(wanted.FileName, null,
                $"Installed {wanted.FileName}, but could not remove {installed.FileName}.");
        }

        return new ModSwapResult(wanted.FileName, installed.FileName, null);
    }

    /// <summary>
    /// A newer build of a mod that is already installed. Carries the file itself rather than a
    /// project id, because finding it is the expensive half and the answer is already in hand.
    /// </summary>
    public sealed record ModUpdate(
        string Path,
        string FromFileName,
        string ToFileName,
        string? Url,
        string? Sha1,
        long Size,
        ModProvider Provider)
    {
        public bool CanApply => Url is { Length: > 0 };
    }

    /// <summary>
    /// What an instance has, and whether each of them is the newest build it could be running.
    ///
    /// Two questions rather than one because the buttons need both. "Do you have this mod" alone
    /// would hide Add on a mod sitting three versions behind, which is the one time it is most
    /// worth pressing.
    /// </summary>
    public sealed class InstanceContents(InstalledMods installed, HashSet<string> outdated)
    {
        public static readonly InstanceContents Empty = new(InstalledMods.Empty, []);

        /// <summary>Installed, and there is nothing newer to move to — so nothing left to offer.</summary>
        public bool HasNewestOf(CatalogueMod mod) =>
            installed.Find(mod) is { } entry && !outdated.Contains(entry.Path);

        public bool Has(CatalogueMod mod) => installed.Has(mod);

        /// <summary>
        /// The same knowledge of what is behind, over a fresh look at what is installed.
        ///
        /// For the moment after an add, when the folder has changed but the world has not: a mod
        /// that was three versions behind a minute ago still is, and hashing the whole folder
        /// again to rediscover that would make every add slower than the one before it.
        /// </summary>
        public InstanceContents WithInstalled(InstalledMods rescanned) => new(rescanned, outdated);
    }

    /// <summary>
    /// Whether an instance has this one mod at the newest build it could run.
    ///
    /// The single-mod form of <see cref="ReadContentsAsync"/>, for a page that is about one mod:
    /// it hashes the one jar rather than the whole folder. False when the mod is not installed at
    /// all, and true when it is installed and nothing can be reached to say otherwise.
    /// </summary>
    public async Task<bool> HasNewestOfAsync(
        Instance instance, CatalogueMod mod, CancellationToken cancellationToken = default)
    {
        if (InstalledMods.For(Paths, instance).Find(mod) is not { } entry) return false;

        try
        {
            var updates = await FindUpdatesAsync(instance, [entry], cancellationToken).ConfigureAwait(false);

            return !updates.Any(update => update.CanApply);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// Reads an instance's folders and asks which of what it holds has moved on since.
    ///
    /// The update half is allowed to fail quietly. Offline, everything installed counts as
    /// current: offering to fetch a newer build that cannot be reached is worse than not
    /// mentioning one, and the mod's own page still lists every build either way.
    /// </summary>
    public async Task<InstanceContents> ReadContentsAsync(
        Instance instance, CancellationToken cancellationToken = default)
    {
        var installed = InstalledMods.For(Paths, instance);
        var outdated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var mods = ModScanner.Scan(ModScanner.ModsDirectory(Paths, instance.Folder));

            foreach (var update in await FindUpdatesAsync(instance, mods, cancellationToken).ConfigureAwait(false))
                if (update.CanApply)
                    outdated.Add(update.Path);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Nothing reachable to compare against. See the note above.
        }

        return new InstanceContents(installed, outdated);
    }

    /// <summary>
    /// Looks over an instance's mods folder for builds newer than what is in it.
    ///
    /// Modrinth answers for the whole folder in one request, which is most of why this is worth
    /// doing at all. CurseForge needs a fingerprint lookup for the ids and then one request per
    /// mod it recognises — so it is asked second, and only about the jars Modrinth did not claim.
    /// </summary>
    public async Task<IReadOnlyList<ModUpdate>> FindUpdatesAsync(
        Instance instance,
        IReadOnlyList<ModEntry> mods,
        CancellationToken cancellationToken = default)
    {
        if (mods.Count == 0) return [];

        var updates = new List<ModUpdate>();
        var hashes = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            var sha1 = await Downloader.Sha1Async(mod.Path, cancellationToken).ConfigureAwait(false);
            hashes.TryAdd(sha1, mod);
        }

        var fromModrinth = await Modrinth
            .GetUpdatesAsync([.. hashes.Keys], instance.MinecraftVersion, instance.Loader, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (hash, version) in fromModrinth)
        {
            if (!hashes.TryGetValue(hash, out var mod)) continue;

            hashes.Remove(hash);

            if (!IsNewer(mod, version.FileName)) continue;

            updates.Add(new ModUpdate(
                mod.Path, mod.FileName, version.FileName,
                version.Url, version.Sha1, version.Size, ModProvider.Modrinth));
        }

        if (!CurseForge.IsAvailable || hashes.Count == 0) return updates;

        // Whatever is left is either CurseForge's or nobody's.
        var remaining = hashes.Values.ToList();
        var prints = new Dictionary<uint, ModEntry>();

        foreach (var mod in remaining)
        {
            var print = await CurseForgeFingerprint.OfFileAsync(mod.Path, cancellationToken).ConfigureAwait(false);
            prints.TryAdd(print, mod);
        }

        var identified = await CurseForge
            .GetModIdsByFingerprintAsync([.. prints.Keys], cancellationToken)
            .ConfigureAwait(false);

        foreach (var (print, modId) in identified)
        {
            if (!prints.TryGetValue(print, out var mod)) continue;

            var download = await CurseForge
                .GetDownloadAsync(
                    modId, instance.MinecraftVersion, instance.Loader, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (download is null || !IsNewer(mod, download.FileName)) continue;

            updates.Add(new ModUpdate(
                mod.Path, mod.FileName, download.FileName,
                download.Url, download.Sha1, download.Size, ModProvider.CurseForge));
        }

        return updates;
    }

    /// <summary>
    /// A different file name is the whole test. Comparing version strings would mean parsing
    /// every mod author's idea of one, and the provider has already answered the only question
    /// that matters: is the newest build for this instance the one that is installed?
    /// </summary>
    private static bool IsNewer(ModEntry installed, string candidate) =>
        !string.Equals(
            System.IO.Path.GetFileNameWithoutExtension(installed.FileName).TrimEnd(),
            System.IO.Path.GetFileNameWithoutExtension(candidate).TrimEnd(),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts a newer build in and takes the old one out. In that order: deleting first would turn
    /// a failed download into a missing mod, which is worse than an out-of-date one.
    /// </summary>
    public async Task<ModSwapResult> ApplyUpdateAsync(
        Instance instance, ModUpdate update, CancellationToken cancellationToken = default)
    {
        if (update.Url is not { Length: > 0 } url)
            return new ModSwapResult(null, null, "The author allows downloads from their page only.");

        var directory = ModScanner.ModsDirectory(Paths, instance.Folder);
        Directory.CreateDirectory(directory);

        await _downloader.RunAsync(
            [new DownloadTask(url, Path.Combine(directory, update.ToFileName), update.Sha1, update.Size)],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {
            if (File.Exists(update.Path)) File.Delete(update.Path);
        }
        catch (IOException)
        {
            return new ModSwapResult(update.ToFileName, null,
                $"Installed {update.ToFileName}, but could not remove {update.FromFileName}.");
        }

        return new ModSwapResult(update.ToFileName, update.FromFileName, null);
    }

    /// <summary>
    /// Works out which shop an installed jar came from, and its id there.
    ///
    /// The two identify a file completely differently — Modrinth by SHA-1, CurseForge by a
    /// Murmur2 fingerprint of its own devising — so both are asked. Modrinth first only because
    /// it needs no API key: where a mod is on both, either answer would do.
    /// </summary>
    private async Task<(IModSource? Source, string? Id)> IdentifyAsync(
        string path, CancellationToken cancellationToken)
    {
        var sha1 = await Downloader.Sha1Async(path, cancellationToken).ConfigureAwait(false);

        if (await Modrinth.GetProjectIdByHashAsync(sha1, cancellationToken).ConfigureAwait(false) is { } project)
            return (Modrinth, project);

        if (!CurseForge.IsAvailable) return (null, null);

        var fingerprint = await CurseForgeFingerprint.OfFileAsync(path, cancellationToken).ConfigureAwait(false);

        return await CurseForge.GetModIdByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false) is { } mod
            ? (CurseForge, mod)
            : (null, null);
    }

    /// <summary>
    /// What would happen to each installed mod if this instance moved to another loader.
    ///
    /// Nothing is changed here — this is the list to put in front of someone before anything is
    /// downloaded or deleted, because "swap my loader" quietly rewriting a mods folder is how
    /// people lose setups they cannot rebuild.
    ///
    /// Three outcomes per mod: the same project publishes for the new loader (most of them), the
    /// project has a port under another name (Sodium becoming Embeddium), or there is nothing and
    /// it has to be left behind.
    /// </summary>
    /// <param name="toVersion">
    /// The Minecraft version to find builds for, or null to keep the instance's own. A duplicate
    /// can change both at once, and the two questions are really one: what does this mod publish
    /// for this pairing. Nothing about the search cared which of them moved.
    /// </param>
    public async Task<IReadOnlyList<ModMove>> PlanLoaderMoveAsync(
        Instance instance, string toLoader, string? toVersion = null,
        CancellationToken cancellationToken = default)
    {
        var directory = ModScanner.ModsDirectory(Paths, instance.Folder);
        var gameVersion = toVersion is { Length: > 0 } wanted ? wanted : instance.MinecraftVersion;

        // The display name, since a row reads "no NeoForge build" rather than "no neoforge".
        var loaderName = new Instance { Id = "", Name = "", MinecraftVersion = "", Loader = toLoader }.LoaderName;

        var installed = ModScanner.Scan(directory);
        var moves = new ModMove[installed.Count];

        // Each mod is an independent question — identify the jar, ask its provider what it has —
        // and asking them one after another means a folder of twenty mods waits for forty round
        // trips in a row. Bounded rather than unbounded: a burst of one request per mod is how a
        // launcher gets itself rate-limited by the shop it depends on.
        await Parallel.ForAsync(0, installed.Count,
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                moves[index] = await PlanMoveAsync(
                    installed[index], gameVersion, toLoader, loaderName, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return moves;
    }

    private async Task<ModMove> PlanMoveAsync(
        ModEntry mod, string gameVersion, string toLoader, string loaderName, CancellationToken cancellationToken)
    {
        ModMove Stuck() => new(mod, null, null, loaderName, gameVersion);

        var (source, projectId) = await IdentifyAsync(mod.Path, cancellationToken).ConfigureAwait(false);

        // Neither shop knows this jar — a hand-built mod, or one taken down. It stays where it is
        // and gets named, rather than being deleted on the way past.
        if (source is null || projectId is null) return Stuck();

        var versions = await source.GetVersionsAsync(projectId, cancellationToken).ConfigureAwait(false);

        if (BestFor(versions, gameVersion, toLoader) is { } direct)
            return new ModMove(mod, direct, mod.Name, loaderName, gameVersion);

        // The same project has nothing for the new loader. It may still exist over there under
        // another name — that table is keyed by Modrinth slug, so this is the one shop that can
        // answer it.
        var slug = source.Provider == ModProvider.Modrinth
            ? (await Modrinth.GetIdentityAsync(projectId, cancellationToken).ConfigureAwait(false))?.Slug
            : null;

        if (slug is null || LoaderCounterparts.For(slug, toLoader) is not { } counterpart) return Stuck();

        var ported = await Modrinth.GetVersionsAsync(counterpart, cancellationToken).ConfigureAwait(false);

        if (BestFor(ported, gameVersion, toLoader) is not { } replacement) return Stuck();

        var identity = await Modrinth.GetIdentityAsync(counterpart, cancellationToken).ConfigureAwait(false);

        return new ModMove(mod, replacement, identity?.Title ?? counterpart, loaderName, gameVersion);
    }

    /// <summary>
    /// The build to move to: one that runs on the new loader and this Minecraft version, a
    /// release ahead of a prerelease, then newest. Same order the conflict swap uses, and for the
    /// same reason — a stable build is the one most of the rest of the pack was tested against.
    /// </summary>
    private static ModVersion? BestFor(IReadOnlyList<ModVersion> versions, string gameVersion, string loader) =>
        versions
            .Where(version => version.CanDownload)
            .Where(version => version.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase))
            .Where(version => version.Loaders.Any(name => name.Equals(loader, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(version => version.Channel == ModChannel.Release)
            .ThenByDescending(version => version.VersionNumber, Comparer<string>.Create(VersionBound.Compare))
            .FirstOrDefault();

    /// <summary>
    /// Finds the mod a loader said was missing, in whichever catalogue has it.
    ///
    /// The loader's id is tried as a slug first, since mod authors overwhelmingly use the same
    /// word for both, and an exact hit needs no guessing at all. Only when that fails is the
    /// name searched for — and the result is checked against the id before being offered, because
    /// installing the wrong mod because its title was similar is worse than installing nothing.
    /// </summary>
    public async Task<CatalogueMod?> FindDependencyAsync(
        Instance instance, MissingDependency missing, CancellationToken cancellationToken = default)
    {
        if (await Modrinth.GetIdentityAsync(missing.Id, cancellationToken).ConfigureAwait(false) is { } exact)
        {
            var listings = await Modrinth.GetProjectsAsync([exact.Slug], cancellationToken).ConfigureAwait(false);

            if (listings.FirstOrDefault() is { } listing) return new CatalogueMod(listing, null);
        }

        var query = new ModQuery(
            missing.Name, instance.MinecraftVersion, instance.Loader, ModSort.Relevance,
            null, 5, 0, ModKind.Mod);

        var results = await Mods.SearchAsync(query, cancellationToken).ConfigureAwait(false);

        // Named closely enough to be the same thing. Compared on letters and digits so
        // "Fabric API" matches "fabric-api", and nothing looser than that.
        return results.FirstOrDefault(mod =>
            Simplify(mod.Title) == Simplify(missing.Id) || Simplify(mod.Title) == Simplify(missing.Name));

        static string Simplify(string text) => new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
    }

    /// <summary>Where a prefetched replacement waits until it is asked for.</summary>
    private string StagedMovePath(ModVersion target) =>
        Path.Combine(Paths.Cache, "moves", target.FileName);

    /// <summary>
    /// Fetches the replacements ahead of being asked for them, so pressing the button copies a
    /// file rather than waiting on a download.
    ///
    /// Into the cache, never into the instance: this runs on a choice nobody has committed to,
    /// and a Cancel has to leave the mods folder exactly as it found it. What is left behind is
    /// a few jars in the cache, which is where jars are supposed to accumulate.
    /// </summary>
    public async Task PrefetchMovesAsync(
        IReadOnlyList<ModMove> moves, CancellationToken cancellationToken = default)
    {
        var tasks = moves
            .Where(move => move.CanMove)
            .Select(move => new DownloadTask(
                move.Target!.Url!, StagedMovePath(move.Target), move.Target.Sha1, move.Target.Size))
            .ToList();

        if (tasks.Count == 0) return;

        Directory.CreateDirectory(Path.Combine(Paths.Cache, "moves"));

        // The downloader already skips anything of the right size that is already there, so a
        // second pass over the same plan costs nothing.
        await _downloader.RunAsync(tasks, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Carries out one move: the replacement is downloaded first and the old jar removed only
    /// once it is on disk, so a failed download leaves the instance as it was rather than short
    /// of a mod.
    /// </summary>
    public async Task<ModSwapResult> ApplyMoveAsync(
        Instance instance, ModMove move, CancellationToken cancellationToken = default)
    {
        if (move.Target is not { Url.Length: > 0 } target)
            return new ModSwapResult(null, null, $"Nothing to move {move.Name} to.");

        var directory = ModScanner.ModsDirectory(Paths, instance.Folder);
        Directory.CreateDirectory(directory);

        // A mod that was switched off stays switched off. Moving loaders is not the moment to
        // quietly turn something back on.
        var fileName = move.Installed.Enabled
            ? target.FileName
            : target.FileName + ModScanner.DisabledSuffix;
        var destination = Path.Combine(directory, fileName);

        // Prefetched while the settings sheet was open, in the usual case.
        var staged = StagedMovePath(target);

        if (File.Exists(staged) && (target.Size <= 0 || new FileInfo(staged).Length == target.Size))
            File.Copy(staged, destination, overwrite: true);
        else
            await _downloader.RunAsync(
                [new DownloadTask(target.Url!, destination, target.Sha1, target.Size)],
                cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {
            if (!string.Equals(move.Installed.Path, destination, StringComparison.OrdinalIgnoreCase))
                File.Delete(move.Installed.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ModSwapResult(fileName, null,
                $"Installed {fileName}, but could not remove {move.Installed.FileName}.");
        }

        return new ModSwapResult(fileName, move.Installed.FileName, null);
    }

    /// <summary>
    /// Points the instance at another loader and records the build to use. The mods are a
    /// separate question, asked separately — see <see cref="PlanLoaderMoveAsync"/>.
    /// </summary>
    public async Task<string?> SetLoaderAsync(
        Instance instance, string loader, CancellationToken cancellationToken = default)
    {
        if (loader.Equals(Minecraft.Loaders.Vanilla, StringComparison.OrdinalIgnoreCase))
        {
            instance.Loader = Minecraft.Loaders.Vanilla;
            instance.LoaderVersion = null;
            Instances.Save(instance);

            return null;
        }

        var version = loader.ToLowerInvariant() switch
        {
            Minecraft.Loaders.Fabric => await Fabric.GetLatestLoaderAsync(instance.MinecraftVersion, cancellationToken)
                .ConfigureAwait(false),
            "quilt" => await Quilt.GetLatestLoaderAsync(instance.MinecraftVersion, cancellationToken)
                .ConfigureAwait(false),
            Minecraft.Loaders.Forge => await Loaders.GetForgeVersionAsync(instance.MinecraftVersion, cancellationToken)
                .ConfigureAwait(false),
            Minecraft.Loaders.NeoForge => await Loaders.GetNeoForgeVersionAsync(instance.MinecraftVersion, cancellationToken)
                .ConfigureAwait(false),
            _ => null,
        };

        if (version is not { Length: > 0 })
            return $"No {loader} build exists for Minecraft {instance.MinecraftVersion}.";

        instance.Loader = loader;
        instance.LoaderVersion = version;
        Instances.Save(instance);

        return null;
    }

    /// <summary>
    /// The most recent log for an instance. Named by the launcher when the game starts, so the
    /// newest one for that id is the run that has just finished.
    /// </summary>
    public string? LatestLogFor(Instance instance)
    {
        try
        {
            return new DirectoryInfo(Paths.Logs)
                .GetFiles($"{instance.Id}-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Opens a link in the user's own browser.</summary>
    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    /// <summary>Opens a folder in the system file manager.</summary>
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
}
