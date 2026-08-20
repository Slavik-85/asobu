using System;
using System.Threading.Tasks;
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

    /// <summary>The version waiting to be installed, once there is one.</summary>
    [ObservableProperty] public partial string? NewVersion { get; set; }

    /// <summary>What went wrong, in words worth showing.</summary>
    [ObservableProperty] public partial string? Error { get; set; }

    /// <summary>This build's own version, for the Settings page to show.</summary>
    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ?? "development build";

    /// <summary>
    /// False when running from a plain build directory, where there is nothing to update. The
    /// Settings card hides itself rather than offering a button that cannot work.
    /// </summary>
    public bool CanUpdate => _manager?.IsInstalled ?? false;

    public bool IsChecking => Stage is UpdateStage.Checking or UpdateStage.Downloading;
    public bool IsReady => Stage == UpdateStage.Ready;
    public bool IsCurrent => Stage == UpdateStage.Current;
    public bool HasFailed => Stage == UpdateStage.Failed;

    public string StatusLine => Stage switch
    {
        UpdateStage.Checking => "Looking for an update…",
        UpdateStage.Downloading => $"Downloading {NewVersion}…",
        UpdateStage.Ready => $"Asobu {NewVersion} is ready.",
        UpdateStage.Current => "Asobu is up to date.",
        UpdateStage.Failed => Error ?? "Couldn't check for updates.",
        _ => $"Asobu {CurrentVersion}",
    };

    partial void OnStageChanged(UpdateStage value)
    {
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(StatusLine));
    }

    partial void OnNewVersionChanged(string? value) => OnPropertyChanged(nameof(StatusLine));

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

    /// <summary>
    /// Applies what has been downloaded and comes back up on the new version. Only ever reached
    /// by someone pressing the button that says so.
    /// </summary>
    [RelayCommand]
    private void Restart()
    {
        if (_manager is not { } manager || _pending is null) return;

        try
        {
            manager.ApplyUpdatesAndRestart(_pending);
        }
        catch (Exception e)
        {
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
