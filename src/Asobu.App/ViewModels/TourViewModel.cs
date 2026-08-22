using System;
using System.Collections.Generic;
using Asobu.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>
/// One stop on the tour. The target is named rather than referenced: a view model that held
/// controls would be a view model that has to be built after the window, and the point of the
/// tour is that it describes the launcher, not the widgets.
/// </summary>
/// <param name="TargetName">x:Name of the control to spotlight, searched for in the window.</param>
/// <param name="Title">The heading on the card.</param>
/// <param name="Body">One or two sentences. Longer than that is a manual, not a tour.</param>
public sealed record TourStep(string TargetName, string Title, string Body)
{
    /// <summary>
    /// A second control to look for when the first one isn't on screen. Some things appear
    /// twice in different states — the New instance button sits in the header once there are
    /// instances and in the middle of the page while there are none — and the tour wants
    /// whichever one the user is actually looking at.
    /// </summary>
    public string? OrElse { get; init; }
}

/// <summary>
/// The guided walk around the launcher, offered once after the welcome.
///
/// Every stop points at something that is on screen at the time, which is why the tour stays on
/// the library page: a step that had to open a modal to point inside it would be a step that
/// leaves the launcher somewhere strange if it is skipped halfway.
/// </summary>
public partial class TourViewModel : ViewModelBase
{
    private readonly AsobuLauncher _launcher;
    private readonly Action _goInstances;

    public TourViewModel(AsobuLauncher launcher, Action goInstances)
    {
        _launcher = launcher;
        _goInstances = goInstances;
    }

    private static readonly IReadOnlyList<TourStep> Script =
    [
        new("NavInstances", "Your instances live here",
            "Every setup you make is its own instance — its own version, its own mods, its own worlds. Nothing you install in one can break another."),

        new("NewInstanceButton", "Start with a new one",
            "Pick a Minecraft version and a mod loader and you're done. Already have a pack, a folder from another launcher, or a share code? The same button imports it.")
        {
            OrElse = "NewInstanceEmptyButton",
        },

        new("NavExplore", "Explore, when you don't know what you want",
            "The good stuff, sorted by what people actually use. Anything you find here can be added straight to an instance."),

        new("NavBrowse", "Browse, when you do",
            "Search Modrinth and CurseForge together, filtered to a version and a loader so everything you see actually fits."),

        new("FriendsButton", "Your friends, from anywhere",
            "Sign in and you are on the Asobu network. Add people by their Minecraft name, or by name and tag for an offline account, and see who's around."),

        new("AccountChip", "Who you're playing as",
            "Add as many accounts as you like and switch whenever. The one selected here is the one Play uses."),

        new("SettingsButton", "And everything else",
            "Memory, Java, mod loader per instance, and the rest. The defaults are chosen to just work, so this is here for when you want more."),
    ];

    /// <summary>Asking whether they want it at all.</summary>
    [ObservableProperty] public partial bool IsOffering { get; set; }

    /// <summary>Walking through it.</summary>
    [ObservableProperty] public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial TourStep? Current { get; set; }

    private int _index;

    public string StepLabel => $"{_index + 1} of {Script.Count}";
    public bool IsLast => _index >= Script.Count - 1;
    public string NextLabel => IsLast ? "Done" : "Next";

    /// <summary>
    /// Offers the tour, unless that has already happened. Called once the welcome is out of the
    /// way and the launcher proper is on screen.
    /// </summary>
    public void OfferOnce()
    {
        if (_launcher.Settings.TourOffered) return;

        IsOffering = true;
    }

    [RelayCommand]
    private void Start()
    {
        MarkOffered();
        IsOffering = false;

        // Every step points at something on or around the library page.
        _goInstances();

        _index = 0;
        IsRunning = true;
        Show();
    }

    [RelayCommand]
    private void Decline()
    {
        MarkOffered();
        IsOffering = false;
    }

    [RelayCommand]
    private void Next()
    {
        if (IsLast)
        {
            End();
            return;
        }

        _index++;
        Show();
    }

    [RelayCommand]
    private void End()
    {
        IsRunning = false;
        Current = null;
    }

    private void Show()
    {
        Current = Script[_index];
        OnPropertyChanged(nameof(StepLabel));
        OnPropertyChanged(nameof(IsLast));
        OnPropertyChanged(nameof(NextLabel));
    }

    private void MarkOffered()
    {
        if (_launcher.Settings.TourOffered) return;

        _launcher.Settings.TourOffered = true;
        _launcher.SaveSettings();
    }
}
