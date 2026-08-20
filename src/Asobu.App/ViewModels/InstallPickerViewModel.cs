using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>
/// Asks which instance something should go into, then puts it there. The work itself is handed
/// in, so the same sheet serves "add this mod" and "add this exact build" without knowing the
/// difference between them.
/// </summary>
/// <param name="title">What is being installed, for the heading.</param>
/// <param name="install">
/// Does the installing, once an instance has been chosen. Returns null when the mod went in, or
/// why it did not — a refusal is worth reading, and worth being able to try another instance for.
/// </param>
/// <param name="support">
/// Which versions and loaders the thing being installed has builds for, fetched when the sheet
/// opens. It is what sorts the instances into the ones that can take it and the ones that cannot.
/// </param>
public delegate void AskInstall(
    string title,
    Func<Instance, Task<string?>> install,
    Func<CancellationToken, Task<ModSupport>> support);

/// <summary>One instance to choose between.</summary>
public partial class InstanceChoice(Instance instance) : ViewModelBase
{
    public Instance Instance { get; } = instance;

    public string Name => Instance.Name;
    public string LoaderName => Instance.LoaderName;
    public string MinecraftVersion => Instance.MinecraftVersion;
    public string IconEmoji => Instance.IconEmoji;
    public bool HasCustomIcon => Instance.HasCustomIcon;
    public string? IconImagePath => Instance.IconImagePath;

    [ObservableProperty] public partial bool IsChosen { get; set; }

    /// <summary>
    /// Whether the mod has a build for what this instance runs. True until the check comes back,
    /// so the list does not flicker half the instances out from under the pointer.
    /// </summary>
    [ObservableProperty] public partial bool IsCompatible { get; set; } = true;

    /// <summary>Said on hover, since a greyed row that will not explain itself is a puzzle.</summary>
    public string Reason => $"No build for {LoaderName} {MinecraftVersion}";
}

public partial class InstallPickerViewModel(AsobuLauncher launcher, Action newInstance) : ViewModelBase
{
    /// <summary>Matches the sheet's slide in Asobu.axaml; keep the two in step.</summary>
    private const int SlideMilliseconds = 240;

    private Func<Instance, Task<string?>>? _install;
    private IReadOnlyList<InstanceChoice> _all = [];
    private CancellationTokenSource? _checking;

    /// <summary>The ones that can take it.</summary>
    public ObservableCollection<InstanceChoice> Instances { get; } = [];

    /// <summary>And the ones that cannot, kept on the list rather than hidden — an instance
    /// missing entirely reads as a bug, where a greyed one reads as an answer.</summary>
    public ObservableCollection<InstanceChoice> Incompatible { get; } = [];

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial InstanceChoice? Chosen { get; set; }
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial bool IsClosing { get; set; }
    [ObservableProperty] public partial bool IsInstalling { get; set; }
    [ObservableProperty] public partial string? Notice { get; set; }

    /// <summary>While the build list is on its way, so the sheet can say why nothing is greyed yet.</summary>
    [ObservableProperty] public partial bool IsChecking { get; set; }

    public bool CanInstall => Chosen is not null && !IsInstalling;
    public bool HasNotice => Notice is { Length: > 0 };

    /// <summary>Nothing to choose between. The sheet says so rather than showing a blank list.</summary>
    public bool HasNone => _all.Count == 0;

    /// <summary>Searched the list down to nothing, which is a different thing from having none.</summary>
    public bool HasNoMatches => _all.Count > 0 && Instances.Count == 0 && Incompatible.Count == 0;

    public bool HasIncompatible => Incompatible.Count > 0;

    public void Open(
        string title,
        Func<Instance, Task<string?>> install,
        Func<CancellationToken, Task<ModSupport>> support)
    {
        Title = title;
        _install = install;

        _all = [.. launcher.Instances.LoadAll().Select(instance => new InstanceChoice(instance))];

        SearchText = "";
        Notice = null;
        IsInstalling = false;
        IsClosing = false;

        Filter();
        Choose(null);

        IsOpen = true;

        _ = CheckAsync(support);
    }

    /// <summary>
    /// Asks what the thing being installed actually builds for, then sorts the instances by it.
    /// One request, however many instances there are.
    /// </summary>
    private async Task CheckAsync(Func<CancellationToken, Task<ModSupport>> support)
    {
        _checking?.Cancel();
        _checking = new CancellationTokenSource();
        var request = _checking;

        IsChecking = true;

        try
        {
            var supported = await support(request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(_checking, request)) return;

            foreach (var choice in _all)
                choice.IsCompatible = supported.Supports(choice.MinecraftVersion, choice.Instance.Loader);
        }
        catch (Exception)
        {
            // Could not find out, so nothing is greyed. Better to offer an instance that turns
            // out not to work than to withhold one that would have.
        }
        finally
        {
            if (ReferenceEquals(_checking, request)) IsChecking = false;

            Filter();

            // Only once it is known which are worth choosing: preselecting the one instance there
            // is, before finding out it cannot take the mod, would be an odd thing to do.
            if (Chosen is null && Instances.Count == 1) Choose(Instances[0]);
        }
    }

    partial void OnSearchTextChanged(string value) => Filter();
    partial void OnChosenChanged(InstanceChoice? value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    private void Filter()
    {
        var text = SearchText.Trim();

        Instances.Clear();
        Incompatible.Clear();

        foreach (var choice in _all)
        {
            if (text.Length > 0 && !choice.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                continue;

            (choice.IsCompatible ? Instances : Incompatible).Add(choice);
        }

        OnPropertyChanged(nameof(HasNone));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(HasIncompatible));
    }

    [RelayCommand]
    private void Pick(InstanceChoice? choice)
    {
        // The row is disabled in the sheet as well; this is the same rule said where it is true
        // rather than only where it is drawn.
        if (choice is { IsCompatible: true }) Choose(choice);
    }

    private void Choose(InstanceChoice? choice)
    {
        Chosen = choice;

        foreach (var other in _all) other.IsChosen = ReferenceEquals(other, choice);
    }

    /// <summary>
    /// Slides away rather than cutting. The sheet stays mounted for the length of it — dropping
    /// it first would take it off screen before a frame had drawn.
    /// </summary>
    [RelayCommand]
    private async Task CloseAsync()
    {
        if (IsClosing) return;

        IsClosing = true;
        await Task.Delay(SlideMilliseconds);

        IsOpen = false;
        IsClosing = false;
        _install = null;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Chosen is not { } choice || _install is not { } install || IsInstalling) return;

        IsInstalling = true;
        Notice = null;

        try
        {
            Notice = await install(choice.Instance);

            // Closed on success only. A refusal — no build for this loader, an author who
            // forbids third-party downloads — stays on screen next to the list, so another
            // instance is one click away rather than a whole reopening.
            if (Notice is null) await CloseAsync();
        }
        catch (Exception ex)
        {
            Notice = ex.Message;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private async Task NewInstanceAsync()
    {
        await CloseAsync();
        newInstance();
    }
}
