using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Minecraft;
using Asobu.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>
/// Asks what a modpack's instance should be called, then makes it.
/// </summary>
/// <param name="pack">The pack being installed, for the heading and the default name.</param>
/// <param name="version">
/// One particular build, when the request came from the versions table. Null means whichever is
/// current — the Add button on a card does not name a version, so neither does this.
/// </param>
public delegate void AskCreatePack(CatalogueMod pack, ModVersion? version);

/// <summary>
/// The sheet behind that delegate. A modpack is not something to put into an instance — it is an
/// instance — so Add cannot ask which one to use the way a mod's does. What it can ask is the
/// one thing only the person knows: what to call it. Everything else the pack already says.
/// </summary>
public partial class PackInstallViewModel(AsobuLauncher launcher, Action<Instance> onCreated) : ViewModelBase
{
    /// <summary>Matches the sheet's slide in Asobu.axaml; keep the two in step.</summary>
    private const int SlideMilliseconds = 240;

    private CatalogueMod? _pack;
    private ModVersion? _version;
    private CancellationTokenSource? _work;
    private Instance? _created;

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial bool IsClosing { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";

    [ObservableProperty] public partial bool IsWorking { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial double Fraction { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    /// <summary>True once the instance exists, so the sheet can offer to go to it.</summary>
    [ObservableProperty] public partial bool IsDone { get; set; }

    /// <summary>Things worth knowing about what was installed — files skipped, guesses made.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    public bool CanCreate => Name.Trim().Length > 0 && !IsWorking && !IsDone;
    public bool HasError => Error is { Length: > 0 };

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnIsWorkingChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public void Open(CatalogueMod pack, ModVersion? version)
    {
        _pack = pack;
        _version = version;
        _created = null;

        Title = $"Install {pack.Title}";

        // The build's own name where one was picked, since two rows of the table would otherwise
        // open an identical sheet and give no sign which was chosen.
        Subtitle = version is { } chosen
            ? $"{chosen.Name} — this becomes a new instance."
            : "A modpack becomes a new instance of its own.";

        Name = pack.Title;
        Status = "";
        Fraction = 0;
        Error = null;
        IsDone = false;
        IsWorking = false;
        IsClosing = false;
        Notes.Clear();

        IsOpen = true;
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        // Mid-install the button cancels the work instead; the sheet stays until that unwinds.
        if (IsWorking)
        {
            _work?.Cancel();
            return;
        }

        await CloseAsync();
    }

    private async Task CloseAsync()
    {
        if (!IsOpen || IsClosing) return;

        IsClosing = true;
        await Task.Delay(SlideMilliseconds);
        IsClosing = false;
        IsOpen = false;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (_pack is not { } pack || IsWorking || !CanCreate) return;

        var name = Name.Trim();

        Error = null;
        Notes.Clear();
        Status = "Starting";
        Fraction = 0;
        IsWorking = true;

        _work = new CancellationTokenSource();

        var progress = new Progress<InstallProgress>(report =>
        {
            Status = report.Stage;
            Fraction = report.Fraction;
        });

        ImportOutcome outcome;
        try
        {
            outcome = _version is { } version
                ? await launcher.Importer.ImportPackVersionAsync(version, name, progress, _work.Token)
                : await launcher.Importer.ImportPackAsync(pack, name, progress, _work.Token);
        }
        catch (OperationCanceledException)
        {
            IsWorking = false;
            Status = "";
            return;
        }
        catch (Exception ex)
        {
            IsWorking = false;
            Error = ex.Message;
            return;
        }
        finally
        {
            _work?.Dispose();
            _work = null;
        }

        IsWorking = false;

        if (!outcome.Succeeded)
        {
            Error = outcome.Reason;
            return;
        }

        _created = outcome.Instance;
        foreach (var note in outcome.Notes) Notes.Add(note);

        Status = $"{name} is ready";
        Fraction = 1;
        IsDone = true;

        // Nothing to read means nothing to stop for.
        if (Notes.Count == 0) await OpenCreatedAsync();
    }

    [RelayCommand]
    private async Task OpenCreatedAsync()
    {
        await CloseAsync();

        if (_created is { } instance) onCreated(instance);
    }
}
