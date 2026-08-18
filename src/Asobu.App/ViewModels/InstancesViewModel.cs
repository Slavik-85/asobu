using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Minecraft;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

public partial class InstancesViewModel : ViewModelBase
{
    private const string AllGroupsFilter = "All";
    private const string UngroupedFilter = "Ungrouped";

    private readonly AsobuLauncher _launcher;
    private readonly AccountsViewModel _accounts;
    private readonly Action _requestNewInstance;
    private readonly Action<Instance> _requestCrashReports;

    private readonly List<Instance> _all = [];
    private Process? _process;

    public InstancesViewModel(
        AsobuLauncher launcher,
        AccountsViewModel accounts,
        Action requestNewInstance,
        Action<Instance> requestCrashReports)
    {
        _launcher = launcher;
        _accounts = accounts;
        _requestNewInstance = requestNewInstance;
        _requestCrashReports = requestCrashReports;
    }

    public ObservableCollection<Instance> Items { get; } = [];
    public ObservableCollection<string> Groups { get; } = [AllGroupsFilter];
    public IReadOnlyList<string> SortModes { get; } = ["Name", "Last played", "Newest", "Playtime"];

    [ObservableProperty] public partial Instance? Selected { get; set; }
    [ObservableProperty] public partial bool IsDetailOpen { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string SortMode { get; set; } = "Name";
    [ObservableProperty] public partial string SelectedGroupFilter { get; set; } = AllGroupsFilter;

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool ConfirmingDelete { get; set; }

    [ObservableProperty] public partial bool IsRenaming { get; set; }
    [ObservableProperty] public partial string RenameText { get; set; } = "";
    [ObservableProperty] public partial string GroupText { get; set; } = "";
    [ObservableProperty] public partial string EnvironmentVariablesText { get; set; } = "";
    [ObservableProperty] public partial string? DiskUsageLabel { get; set; }
    [ObservableProperty] public partial bool IsIconPickerOpen { get; set; }

    /// <summary>No instances exist at all — as opposed to none matching the current search.</summary>
    public bool IsEmpty => _all.Count == 0;
    public bool HasNoMatches => _all.Count > 0 && Items.Count == 0;
    public bool IsLibraryVisible => !IsDetailOpen;

    public bool CanPlay => Selected is not null && !IsBusy && !IsRunning;
    public string PlayLabel => IsRunning ? "Running" : IsBusy ? "Working…" : "Play";
    public string DeleteLabel => ConfirmingDelete ? "Really delete?" : "Delete";
    public string AccountLabel => _accounts.Active is { } a ? $"as {a.Username}" : "no account selected";
    public IReadOnlyList<string> IconChoices => Instance.IconChoices;

    public void Reload()
    {
        var previous = Selected?.Id;

        _all.Clear();
        _all.AddRange(_launcher.Instances.LoadAll());

        RefreshGroups();
        ApplyFilter();

        if (previous is not null) Selected = _all.FirstOrDefault(i => i.Id == previous);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Guards against a spurious re-entrant filter pass while <see cref="Groups"/> is being
    /// rebuilt: clearing that collection also clears the ComboBox's own selection for an
    /// instant, which would otherwise push a stray null through to <see cref="SelectedGroupFilter"/>
    /// and filter every instance out from under the current selection.
    /// </summary>
    private bool _isRefreshingGroups;

    private void RefreshGroups()
    {
        _isRefreshingGroups = true;
        try
        {
            var current = SelectedGroupFilter;

            var named = _all
                .Select(i => i.Group)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase);

            Groups.Clear();
            Groups.Add(AllGroupsFilter);
            if (_all.Any(i => string.IsNullOrWhiteSpace(i.Group))) Groups.Add(UngroupedFilter);
            foreach (var group in named) Groups.Add(group!);

            SelectedGroupFilter = Groups.Contains(current) ? current : AllGroupsFilter;
        }
        finally
        {
            _isRefreshingGroups = false;
        }
    }

    partial void OnSelectedGroupFilterChanged(string value)
    {
        if (_isRefreshingGroups) return;
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSortModeChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var matches = SelectedGroupFilter switch
        {
            null or AllGroupsFilter => _all.AsEnumerable(),
            UngroupedFilter => _all.Where(i => string.IsNullOrWhiteSpace(i.Group)),
            var group => _all.Where(i => string.Equals(i.Group, group, StringComparison.OrdinalIgnoreCase)),
        };

        var search = SearchText.Trim();
        if (search.Length > 0)
            matches = matches.Where(i =>
                i.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                i.MinecraftVersion.Contains(search, StringComparison.OrdinalIgnoreCase));

        matches = SortMode switch
        {
            "Last played" => matches.OrderByDescending(i => i.LastPlayed ?? DateTimeOffset.MinValue),
            "Newest" => matches.OrderByDescending(i => i.Created),
            "Playtime" => matches.OrderByDescending(i => i.PlaytimeSeconds),
            _ => matches.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };

        Items.Clear();
        foreach (var instance in matches) Items.Add(instance);

        OnPropertyChanged(nameof(HasNoMatches));
    }

    partial void OnSelectedChanged(Instance? value)
    {
        ConfirmingDelete = false;
        IsRenaming = false;
        IsIconPickerOpen = false;
        RenameText = value?.Name ?? "";
        GroupText = value?.Group ?? "";
        EnvironmentVariablesText = value is null ? "" : FormatEnvironment(value.EnvironmentVariables);
        DiskUsageLabel = null;

        OnPropertyChanged(nameof(CanPlay));

        if (value is { } instance) _ = LoadDiskUsageAsync(instance);
    }

    partial void OnIsDetailOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLibraryVisible));
        if (!value) Error = null;
    }

    private async Task LoadDiskUsageAsync(Instance instance)
    {
        var path = _launcher.Paths.InstanceDir(instance.Id);
        var size = await Task.Run(() => DirectorySize.Compute(path));

        if (Selected?.Id == instance.Id) DiskUsageLabel = Format.Bytes(size);
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayLabel));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayLabel));
    }

    partial void OnConfirmingDeleteChanged(bool value) => OnPropertyChanged(nameof(DeleteLabel));

    public void RefreshAccountLabel() => OnPropertyChanged(nameof(AccountLabel));

    // ---- Navigation ----

    [RelayCommand]
    private void OpenInstance(Instance? instance)
    {
        if (instance is null) return;
        Selected = instance;
        IsDetailOpen = true;
    }

    [RelayCommand]
    private void CloseDetail()
    {
        IsDetailOpen = false;
        Reload();
    }

    [RelayCommand]
    private void NewInstance() => _requestNewInstance();

    [RelayCommand]
    private void ViewCrashReports()
    {
        if (Selected is { } instance) _requestCrashReports(instance);
    }

    // ---- Rename ----

    [RelayCommand]
    private void StartRename()
    {
        if (Selected is not { } instance) return;
        RenameText = instance.Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void ConfirmRename()
    {
        if (Selected is not { } instance) return;
        IsRenaming = false;

        var name = RenameText.Trim();
        if (name.Length == 0 || name == instance.Name) return;

        instance.Name = name;
        _launcher.Instances.Save(instance);
        ApplyFilter();
        OnPropertyChanged(nameof(Selected));
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    // ---- Group ----

    partial void OnGroupTextChanged(string value)
    {
        if (Selected is not { } instance) return;

        var group = value.Trim();
        if (group == (instance.Group ?? "")) return;

        instance.Group = group.Length == 0 ? null : group;
        _launcher.Instances.Save(instance);
        RefreshGroups();
        ApplyFilter();
    }

    // ---- Icon ----

    [RelayCommand]
    private void ToggleIconPicker() => IsIconPickerOpen = !IsIconPickerOpen;

    [RelayCommand]
    private void SelectIcon(string? icon)
    {
        if (Selected is not { } instance || icon is null) return;

        instance.Icon = icon;
        _launcher.Instances.Save(instance);
        IsIconPickerOpen = false;
        ApplyFilter();
        OnPropertyChanged(nameof(Selected));
    }

    // ---- Environment variables ----

    partial void OnEnvironmentVariablesTextChanged(string value)
    {
        if (Selected is not { } instance) return;

        instance.EnvironmentVariables = ParseEnvironment(value);
        _launcher.Instances.Save(instance);
    }

    private static string FormatEnvironment(Dictionary<string, string> vars) =>
        string.Join('\n', vars.Select(kv => $"{kv.Key}={kv.Value}"));

    private static Dictionary<string, string> ParseEnvironment(string text)
    {
        var result = new Dictionary<string, string>();

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0) continue;

            result[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
        }

        return result;
    }

    // ---- Clone / export / import ----

    [RelayCommand]
    private void Clone()
    {
        if (Selected is not { } instance) return;

        var clone = _launcher.Instances.Clone(instance);
        Reload();
        Selected = _all.FirstOrDefault(i => i.Id == clone.Id);
    }

    /// <summary>Called from the view's code-behind, which owns the save-file dialog.</summary>
    public async Task<bool> ExportAsync(string destinationPath)
    {
        if (Selected is not { } instance) return false;

        try
        {
            await Task.Run(() => _launcher.Instances.Export(instance, destinationPath));
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Couldn't export: {ex.Message}";
            return false;
        }
    }

    /// <summary>Called from the view's code-behind, which owns the open-file dialog.</summary>
    public async Task<bool> ImportAsync(string sourcePath)
    {
        try
        {
            var imported = await Task.Run(() => _launcher.Instances.Import(sourcePath));
            Reload();
            Selected = _all.FirstOrDefault(i => i.Id == imported.Id);
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Couldn't import: {ex.Message}";
            return false;
        }
    }

    // ---- Play / kill ----

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (Selected is not { } instance) return;

        Error = null;

        if (_accounts.Active is not { } account)
        {
            Error = "Add an account before playing.";
            return;
        }

        IsBusy = true;
        Progress = 0;
        Status = "Preparing";

        try
        {
            var session = await _launcher.ResolveSessionAsync(account);

            var reporter = new Progress<InstallProgress>(p =>
            {
                Status = p.Stage;
                Progress = p.Fraction;
            });

            var startedAt = DateTimeOffset.UtcNow;
            var process = await _launcher.LaunchAsync(instance, session, reporter);

            _process = process;
            IsRunning = true;
            Status = "Minecraft is running";
            Progress = 1;

            _ = TrackAsync(process, instance, startedAt);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Waits for the game off the UI thread, then records playtime.</summary>
    private async Task TrackAsync(Process process, Instance instance, DateTimeOffset startedAt)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (SystemException)
        {
        }

        var exitCode = process.ExitCode;
        var played = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds;

        instance.PlaytimeSeconds += played;
        _launcher.Instances.Save(instance);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ReferenceEquals(_process, process)) _process = null;

            IsRunning = false;
            Status = "";
            // A non-zero exit after the game was up means a crash, not a normal quit.
            if (exitCode != 0)
                Error = $"Minecraft exited with code {exitCode}. Check the crash reports.";

            OnPropertyChanged(nameof(Selected));
            ApplyFilter();
        });
    }

    [RelayCommand]
    private void Kill()
    {
        if (_process is not { } process) return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the button click and here.
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Selected is { } instance)
            AsobuLauncher.OpenFolder(_launcher.Paths.InstanceGameDir(instance.Id));
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } instance) return;

        // Instances hold worlds. One click must never be enough.
        if (!ConfirmingDelete)
        {
            ConfirmingDelete = true;
            return;
        }

        _launcher.Instances.Delete(instance);
        ConfirmingDelete = false;
        Selected = null;
        IsDetailOpen = false;
        Reload();
    }
}
