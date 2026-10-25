using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Asobu.Core;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;
using Velopack.Sources;

namespace Asobu.App.ViewModels;

/// <summary>Where an update has got to. Only two of these are worth showing anyone.</summary>
public enum UpdateStage
{
    /// <summary>Nothing known yet, or this build cannot update itself.</summary>
    Idle,

    Checking,

    /// <summary>Up to date, which is only said out loud when someone asked.</summary>
    Current,

    Downloading,

    /// <summary>Downloaded and staged. Restarting is all that is left.</summary>
    Ready,

    /// <summary>A newer version is out, for an install whose package manager has to fetch it.</summary>
    Available,

    Failed,
}

/// <summary>
/// Keeps Asobu up to date.
///
/// The download happens on its own, quietly, because a launcher that waits to be asked is a
/// launcher running last month's build. What it never does on its own is restart: the point of
/// this application is starting a game that takes a while to start, and pulling the floor out
/// from under someone mid-download to install a version of the launcher they did not ask for is
/// the rudest thing a launcher can do. So it downloads, then says so, and waits.
///
/// Everything here is inert in a build that was not installed — running from `dotnet run` or an
/// unpacked folder has no update path, and Velopack says so rather than guessing.
/// </summary>
public partial class UpdateViewModel : ViewModelBase
{
    /// <summary>
    /// Where releases are published. A public repository, which is what lets this be fetched
    /// with no token: GitHub only serves release assets from a private repo to an authenticated
    /// caller, and a token shipped inside a launcher is not a token, it is a giveaway.
    /// </summary>
    private const string ReleasesUrl = "https://github.com/Slavik-85/asobu";

    private readonly UpdateManager? _manager;

    private UpdateInfo? _pending;

    public UpdateViewModel()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(ReleasesUrl, accessToken: null, prerelease: false));
        }
        catch (Exception)
        {
            // A build with no update path at all. Nothing here is load-bearing.
            _manager = null;
        }
    }

    [ObservableProperty] public partial UpdateStage Stage { get; set; } = UpdateStage.Idle;

    /// <summary>
    /// Covers the window while the update is handed over, so the launcher leaves on purpose
    /// rather than vanishing. A plain number the view eases towards, the same way the welcome
    /// fades — nothing to get stuck in, which matters for something that covers everything.
    /// </summary>
    [ObservableProperty] public partial double Veil { get; set; }

    /// <summary>Whether that curtain is up. Kept apart from Veil so the view can unmount it.</summary>
    [ObservableProperty] public partial bool IsApplying { get; set; }

    /// <summary>The version waiting to be installed, once there is one.</summary>
    [ObservableProperty] public partial string? NewVersion { get; set; }

    /// <summary>What went wrong, in words worth showing.</summary>
    [ObservableProperty] public partial string? Error { get; set; }

    /// <summary>
    /// A .deb, .rpm or tarball install. Velopack cannot update those and must not try — the files
    /// belong to the package manager — so Asobu says when a newer version is out instead.
    /// </summary>
    private static readonly string? Package = AsobuPaths.PackageType();

    /// <summary>This build's own version, for the Settings page to show.</summary>
    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString() ?? (Package is not null ? BuiltVersion() : "development build");

    /// <summary>
    /// False when running from a plain build directory, where there is nothing to update. The
    /// Settings card hides itself rather than offering a button that cannot work.
    /// </summary>
    public bool CanUpdate => _manager?.IsInstalled ?? false;

    /// <summary>A package install, which is told about new versions rather than given them.</summary>
    public bool CanNotify => !CanUpdate && Package is not null;

    public bool ShowCard => CanUpdate || CanNotify;

    public string Blurb => CanNotify
        ? "Installed from a package, so your package manager owns the files. Asobu tells you when a new version is out."
        : "Asobu checks on its own when it starts and downloads quietly in the background. It never restarts without being asked.";

    public bool IsChecking => Stage is UpdateStage.Checking or UpdateStage.Downloading;
    public bool IsReady => Stage == UpdateStage.Ready;
    public bool IsAvailable => Stage == UpdateStage.Available;

    /// <summary>
    /// Whether a game is up, asked of whoever knows. Updating replaces the launcher underneath a
    /// running game and takes it down with the process that started it, so an update mid-session
    /// is somebody losing whatever they had not saved.
    /// </summary>
    public Func<bool>? GamesRunning { get; set; }

    public bool IsBlockedByGame => IsReady && (GamesRunning?.Invoke() ?? false);

    public bool CanRestart => IsReady && !IsBlockedByGame;

    public string RestartLabel => IsBlockedByGame ? "Close Minecraft to update" : "Restart to update";

    /// <summary>Called when a game starts or stops, since neither is something this can see.</summary>
    public void RefreshCanRestart()
    {
        OnPropertyChanged(nameof(IsBlockedByGame));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(RestartLabel));
    }
    public bool IsCurrent => Stage == UpdateStage.Current;
    public bool HasFailed => Stage == UpdateStage.Failed;

    public string StatusLine => Stage switch
    {
        UpdateStage.Checking => "Looking for an update…",
        UpdateStage.Downloading => $"Downloading {NewVersion}…",
        UpdateStage.Ready => $"Asobu {NewVersion} is ready.",
        UpdateStage.Available => $"Asobu {NewVersion} is out.",
        UpdateStage.Current => "Asobu is up to date.",
        UpdateStage.Failed => Error ?? "Couldn't check for updates.",
        _ => $"Asobu {CurrentVersion}",
    };

    partial void OnStageChanged(UpdateStage value)
    {
        // An update is downloaded and waiting for a restart, so there is nothing further to look
        // for. CheckAsync would refuse anyway; stopping means not waking every ten minutes for
        // the rest of the session to be refused.
        if (value is UpdateStage.Ready or UpdateStage.Available)
        {
            _timer?.Stop();
            _timer = null;
        }

        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsAvailable));
        RefreshCanRestart();
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(StatusLine));
    }

    partial void OnNewVersionChanged(string? value) => OnPropertyChanged(nameof(StatusLine));

    /// <summary>
    /// How often a running launcher looks again.
    ///
    /// The startup check used to be the only one, which meant a launcher left open — and this
    /// one is meant to be left open, since it is what you alt-tab back to — never noticed a new
    /// version at all. Somebody with Asobu up for a week stayed a week behind.
    /// </summary>
    private static readonly TimeSpan HowOften = TimeSpan.FromMinutes(10);

    private DispatcherTimer? _timer;

    /// <summary>
    /// Starts looking again every ten minutes.
    ///
    /// Cheap enough not to think about: one request to GitHub's release feed, six times an hour,
    /// against an unauthenticated allowance of sixty. It costs nothing while there is nothing to
    /// find, because a check that finds no update downloads nothing.
    /// </summary>
    public void KeepChecking()
    {
        // Nothing to check in a build that has no update path — a source run, or an unpacked
        // folder. Velopack says so rather than being guessed at.
        if (_timer is not null || !ShowCard) return;

        _timer = new DispatcherTimer { Interval = HowOften };
        _timer.Tick += (_, _) => _ = CheckQuietlyAsync();
        _timer.Start();
    }

    /// <summary>
    /// The startup check. Silent about everything except an update actually being ready: a
    /// launcher that announces "up to date" every time it opens is a launcher being tiresome
    /// about its own maintenance.
    /// </summary>
    public Task CheckQuietlyAsync() => CheckAsync(quiet: true);

    [RelayCommand]
    private Task Check() => CheckAsync(quiet: false);

    private async Task CheckAsync(bool quiet)
    {
        if (CanNotify)
        {
            await CheckReleasesAsync(quiet);
            return;
        }

        if (_manager is not { IsInstalled: true } manager) return;
        if (IsChecking || IsReady) return;

        Error = null;
        Stage = UpdateStage.Checking;

        try
        {
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);

            if (update is null)
            {
                await OnUi(() => Stage = quiet ? UpdateStage.Idle : UpdateStage.Current);
                return;
            }

            await OnUi(() =>
            {
                _pending = update;
                NewVersion = update.TargetFullRelease.Version.ToString();
                Stage = UpdateStage.Downloading;
            });

            // Deltas where they exist, the whole package where they don't. Velopack decides,
            // and falls back to the full release on its own if a patch will not apply.
            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);

            await OnUi(() => Stage = UpdateStage.Ready);
        }
        catch (Exception e)
        {
            await OnUi(() =>
            {
                // Being offline is the ordinary case, not a fault worth interrupting anyone over.
                Error = quiet ? null : Readable(e);
                Stage = quiet ? UpdateStage.Idle : UpdateStage.Failed;
            });
        }
    }

    private static readonly HttpClient Http = new();

    /// <summary>
    /// For a package install: is GitHub's latest release newer than this one. Asks for the
    /// version number and nothing else — the download is the package manager's to do. The same
    /// endpoint skips pre-releases, as Velopack is told to.
    /// </summary>
    private async Task CheckReleasesAsync(bool quiet)
    {
        if (IsChecking || IsAvailable) return;

        Error = null;
        Stage = UpdateStage.Checking;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Slavik-85/asobu/releases/latest");
            request.Headers.UserAgent.ParseAdd("Asobu");

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var tag = release.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v');

            // Only once this install's own file is up. The release goes live with the Windows
            // build, and the Linux packages follow minutes later — or not at all, if packaging
            // failed — and a Download button pointing at nothing is worse than waiting.
            var hasFile = PackageFile is not { } file
                          || release.RootElement.TryGetProperty("assets", out var assets)
                          && assets.EnumerateArray().Any(asset =>
                              asset.TryGetProperty("name", out var name) && name.GetString() == file);

            var newer = hasFile
                        && tag is not null
                        && SemanticVersion.TryParse(tag, out var latest)
                        && SemanticVersion.TryParse(CurrentVersion, out var mine)
                        && latest > mine;

            await OnUi(() =>
            {
                if (newer)
                {
                    NewVersion = tag;
                    Stage = UpdateStage.Available;
                }
                else
                {
                    Stage = quiet ? UpdateStage.Idle : UpdateStage.Current;
                }
            });
        }
        catch (Exception e)
        {
            await OnUi(() =>
            {
                Error = quiet ? null : Readable(e);
                Stage = quiet ? UpdateStage.Idle : UpdateStage.Failed;
            });
        }
    }

    /// <summary>The release file this install updates from, or null when that is a whole release page.</summary>
    private static string? PackageFile => Package switch
    {
        "deb" => "Asobu-linux-x64.deb",
        "rpm" => "Asobu-linux-x64.rpm",
        "tar" => "Asobu-linux-x64.tar.gz",
        _ => null,
    };

    /// <summary>The new package, opened in the browser for the package manager to install.</summary>
    [RelayCommand]
    private void Download() => AsobuLauncher.OpenUrl(Package is "deb" or "rpm"
        ? $"{ReleasesUrl}/releases/latest/download/{PackageFile}"
        : $"{ReleasesUrl}/releases/latest");

    /// <summary>The version this was built as, without the commit hash the SDK appends.</summary>
    private static string BuiltVersion() =>
        typeof(UpdateViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "unknown";

    /// <summary>How long the view takes to bring the curtain up. Keep the two in step.</summary>
    private const int VeilMilliseconds = 420;

    /// <summary>
    /// Applies what has been downloaded and comes back up on the new version. Only ever reached
    /// by someone pressing the button that says so.
    ///
    /// The curtain goes up first and is given time to land. Velopack hands off to Update.exe and
    /// this process ends within moments of asking, so without that beat the window would simply
    /// disappear mid-click — and the thing it disappears into is an installer that spent its
    /// whole life showing this same name on this same background.
    /// </summary>
    [RelayCommand]
    private async Task RestartAsync()
    {
        if (_manager is not { } manager || _pending is null || IsApplying) return;

        // Checked here as well as on the button. The button is the polite half; this is the half
        // that holds when something else calls the command.
        if (IsBlockedByGame) return;

        IsApplying = true;
        Veil = 1;
        await Task.Delay(VeilMilliseconds);

        try
        {
            manager.ApplyUpdatesAndRestart(_pending);
        }
        catch (Exception e)
        {
            // Still here, so the curtain comes back down and says why.
            Veil = 0;
            IsApplying = false;
            Error = Readable(e);
            Stage = UpdateStage.Failed;
        }
    }

    private static string Readable(Exception e) => e switch
    {
        System.Net.Http.HttpRequestException => "Couldn't reach GitHub. Check your connection.",
        TaskCanceledException => "The update timed out.",
        _ => e.Message,
    };

    /// <summary>Velopack's work happens off the UI thread; everything bound to lives on it.</summary>
    private static Task OnUi(Action work) =>
        Dispatcher.UIThread.InvokeAsync(work).GetTask();
}
