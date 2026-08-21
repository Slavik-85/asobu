using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Diagnostics;
using Asobu.Core.Instances;
using Asobu.Core.Java;
using Asobu.Core.Launch;
using Asobu.Core.Minecraft;
using Asobu.App.Controls;
using Asobu.Core.Mods;
using Asobu.Core.Online;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>
/// One option in the banner picker. A null <see cref="Token"/> is the "pick one for me" entry,
/// which is what an instance that was never customised is already using.
/// </summary>
public partial class BannerChoice(string? token, string label, Bitmap? preview) : ViewModelBase
{
    public string? Token { get; } = token;
    public string Label { get; } = label;
    public Bitmap? Preview { get; } = preview;

    public bool HasPreview => Preview is not null;

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>
/// One file the person has to fetch themselves, and how far along that is. Its own object rather
/// than a line of text: each row moves independently as its download lands.
/// </summary>
public partial class BlockedDownloadRow(BlockedDownload item) : ViewModelBase
{
    public BlockedDownload Item { get; } = item;

    public string ModName { get; } = item.ModName;
    public string FileName { get; } = item.FileName;

    [ObservableProperty] public partial bool IsDone { get; set; }

    /// <summary>True once the page has been opened, so the row can say it is waiting.</summary>
    [ObservableProperty] public partial bool IsWaiting { get; set; }

    public string Status => IsDone ? "Added" : IsWaiting ? "Waiting for the download" : "Not yet";

    partial void OnIsDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsPending));
    }

    partial void OnIsWaitingChanged(bool value) => OnPropertyChanged(nameof(Status));

    public bool IsPending => !IsDone;
}

/// <summary>One collapsible band of the library, the way Prism lays it out.</summary>
public partial class InstanceGroup(string name, bool canDelete) : ViewModelBase
{
    public string Name { get; } = name;

    /// <summary>False for the Ungrouped band, which isn't a group anyone made.</summary>
    public bool CanDelete { get; } = canDelete;
    public ObservableCollection<Instance> Items { get; } = [];

    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;

    /// <summary>True only while the band is folding away, so it stays mounted to animate.</summary>
    [ObservableProperty] public partial bool IsCollapsing { get; set; }
}

public partial class InstancesViewModel : ViewModelBase
{
    private const string AllGroupsFilter = "All";
    private const string UngroupedFilter = "Ungrouped";

    /// <summary>
    /// Sentinel for "this instance has no Java preference of its own". Distinct from "auto",
    /// which is itself a choice — an instance pinned to auto stays on auto even if the launcher
    /// default is later pointed at a specific runtime.
    /// </summary>
    private const string InheritJava = "%inherit%";

    private readonly AsobuLauncher _launcher;
    private readonly AccountsViewModel _accounts;
    private readonly Action _requestNewInstance;
    private readonly Action<Instance> _requestCrashReports;
    private readonly Action<Instance, ModKind> _requestAddMods;

    private readonly List<Instance> _all = [];
    private Process? _process;

    public InstancesViewModel(
        AsobuLauncher launcher,
        AccountsViewModel accounts,
        Action requestNewInstance,
        Action<Instance> requestCrashReports,
        Action<Instance, ModKind> requestAddMods)
    {
        _launcher = launcher;
        _accounts = accounts;
        _requestNewInstance = requestNewInstance;
        _requestCrashReports = requestCrashReports;
        _requestAddMods = requestAddMods;

        MaximumMemoryMb = Math.Max(2048, LauncherSettings.SystemMemoryMb());

        // Settled here rather than on first use. Defaulting it inside LoadModsAsync meant the
        // first load assigned it, the change handler started a second load, and both appended to
        // the same list — every row twice.
        ContentKind = ContentKinds[0];
    }

    /// <summary>The flat filtered list. Kept because the empty states count against it.</summary>
    public ObservableCollection<Instance> Items { get; } = [];

    /// <summary>The same instances, banded by group — this is what the library actually draws.</summary>
    public ObservableCollection<InstanceGroup> InstanceGroups { get; } = [];
    public ObservableCollection<string> Groups { get; } = [AllGroupsFilter];
    public ObservableCollection<ModRowViewModel> Mods { get; } = [];

    // ---- Which kind of content the card lists. One table serves all five: they differ in the
    // folder they read and in whether a row can be updated or turned off, not in their shape.

    /// <summary>What the dropdown offers, in the order the folders matter to most people.</summary>
    public IReadOnlyList<KindOption> ContentKinds { get; } =
    [
        new(ModKind.Mod, "Mods"),
        new(ModKind.ResourcePack, "Resource packs"),
        new(ModKind.Shader, "Shader packs"),
        new(ModKind.DataPack, "Data packs"),
        new(ModKind.World, "Worlds"),

        // Last, because it is the one you pick when none of the others is what you meant.
        new(ModKind.Any, "Everything"),
    ];

    [ObservableProperty] public partial KindOption? ContentKind { get; set; }

    private ModKind Kind => ContentKind?.Value ?? ModKind.Mod;

    /// <summary>Its own noun, so an empty shader folder does not say "0 mods".</summary>
    public string ContentCountLabel => Kind == ModKind.Any
        ? Mods.Count == 1 ? "1 item" : $"{Mods.Count} items"
        : Mods.Count == 1
            ? $"1 {Singular}"
            : $"{Mods.Count} {ContentKind?.Label.ToLowerInvariant() ?? "mods"}";

    private string Singular => Kind switch
    {
        ModKind.ResourcePack => "resource pack",
        ModKind.Shader => "shader pack",
        ModKind.DataPack => "data pack",
        ModKind.World => "world",
        _ => "mod",
    };

    public string AddContentLabel => Kind switch
    {
        ModKind.Any => "Add anything",
        ModKind.Mod => "Add mods",
        _ => $"Add {ContentKind!.Label.ToLowerInvariant()}",
    };

    public string OpenFolderLabel => Kind switch
    {
        // Everything spans five folders, so the one worth opening is the one holding them.
        ModKind.Any => "Open game folder",
        ModKind.ResourcePack => "Open resourcepacks folder",
        ModKind.Shader => "Open shaderpacks folder",
        ModKind.DataPack => "Open datapacks folder",
        ModKind.World => "Open saves folder",
        _ => "Open mods folder",
    };

    /// <summary>
    /// What an empty folder says. Worlds get their own wording rather than "none installed":
    /// someone with ten worlds of their own would read that as the launcher having lost them,
    /// when the list is only ever showing the ones it downloaded itself.
    /// </summary>
    public string EmptyTitle => Kind switch
    {
        ModKind.Any => "Nothing installed",
        ModKind.ResourcePack => "No resource packs installed",
        ModKind.Shader => "No shader packs installed",
        ModKind.DataPack => "No data packs installed",
        ModKind.World => "No downloaded worlds",
        _ => "No mods installed",
    };

    public string EmptyDetail => Kind switch
    {
        ModKind.Any =>
            "Mods, resource packs, shaders, data packs and downloaded worlds all show up here together.",
        ModKind.ResourcePack =>
            "Add some from Modrinth and CurseForge, or drop them straight into the resourcepacks folder and they show up here.",
        ModKind.Shader =>
            "Add some from Modrinth and CurseForge, or drop them straight into the shaderpacks folder. Shaders need Iris or OptiFine to do anything.",
        ModKind.DataPack =>
            "Add some from Modrinth and CurseForge, or drop them straight into the datapacks folder. A data pack only takes effect in a world that loads it.",
        ModKind.World =>
            "Worlds you download show up here. The ones you made yourself are still in the game — this list leaves them alone.",
        _ => "Add some from Modrinth and CurseForge, or drop jars straight into the mods folder and they show up here.",
    };

    /// <summary>
    /// Worlds are listed but never toggled: the game reads every folder in saves/ that has a
    /// level.dat, so renaming one aside does not disable it the way it does a mod.
    /// </summary>
    public bool CanToggleContent => Kind != ModKind.World;

    /// <summary>
    /// And only mods are checked for updates. The rest can be, in principle — the lookup is by
    /// file hash — but nothing else has a version people track, and hashing a folder of worlds
    /// to ask about them would be work nobody asked for.
    /// </summary>
    private bool ChecksUpdates => Kind == ModKind.Mod;

    partial void OnContentKindChanged(KindOption? value)
    {
        OnPropertyChanged(nameof(ContentCountLabel));
        OnPropertyChanged(nameof(AddContentLabel));
        OnPropertyChanged(nameof(CanToggleContent));
        OnPropertyChanged(nameof(OpenFolderLabel));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDetail));

        if (Selected is { } instance) _ = LoadModsAsync(instance);
    }
    public ObservableCollection<BannerChoice> BannerChoices { get; } = [];
    public ObservableCollection<JavaOption> JavaOptions { get; } = [];
    public IReadOnlyList<string> SortModes { get; } = ["Name", "Last played", "Newest", "Playtime"];

    public int MaximumMemoryMb { get; }

    [ObservableProperty] public partial Instance? Selected { get; set; }
    [ObservableProperty] public partial bool IsDetailOpen { get; set; }

    /// <summary>True only while the sheet is sliding back down, so it stays mounted to animate.</summary>
    [ObservableProperty] public partial bool IsDetailClosing { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string SortMode { get; set; } = "Name";
    [ObservableProperty] public partial string SelectedGroupFilter { get; set; } = AllGroupsFilter;

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    /// <summary>
    /// The instance a delete is being confirmed for. Held separately from Selected: the context
    /// menu can target a card that was never opened.
    /// </summary>
    [ObservableProperty] public partial Instance? PendingDelete { get; set; }
    [ObservableProperty] public partial bool IsDeleteConfirmOpen { get; set; }
    [ObservableProperty] public partial bool IsDeleteConfirmClosing { get; set; }

    /// <summary>
    /// Groups an instance can be put into: "Ungrouped" plus every named group in use. Distinct
    /// from <see cref="Groups"/>, which is the library filter and carries an "All" entry that
    /// makes no sense as an assignment.
    /// </summary>
    public ObservableCollection<string> AssignableGroups { get; } = [];

    [ObservableProperty] public partial string? SelectedGroup { get; set; }
    [ObservableProperty] public partial bool IsNewGroupOpen { get; set; }
    [ObservableProperty] public partial string NewGroupName { get; set; } = "";

    /// <summary>
    /// Why the typed name cannot be used, or null. Two names are the launcher's own: "Ungrouped"
    /// is what having no group is called, and "Pinned" is what the right-click menu manages. A
    /// group under either name would be a second control over the same thing, disagreeing with
    /// the first.
    ///
    /// Said rather than silently ignored: a Create button that does nothing when pressed is a
    /// bug from where the person is sitting, whatever the code intended.
    /// </summary>
    public string? NewGroupProblem => NewGroupName.Trim() switch
    {
        { Length: 0 } => null,
        var name when name.Equals(UngroupedFilter, StringComparison.OrdinalIgnoreCase) =>
            "“Ungrouped” is what an instance with no group is already called.",
        var name when name.Equals(Instance.PinnedGroup, StringComparison.OrdinalIgnoreCase) =>
            "“Pinned” is managed by the right-click menu. Use Pin on the instance instead.",
        _ => null,
    };

    public bool CanCreateGroup => NewGroupName.Trim().Length > 0 && NewGroupProblem is null;

    public bool HasNewGroupProblem => NewGroupProblem is { Length: > 0 };

    partial void OnNewGroupNameChanged(string value)
    {
        OnPropertyChanged(nameof(NewGroupProblem));
        OnPropertyChanged(nameof(HasNewGroupProblem));
        OnPropertyChanged(nameof(CanCreateGroup));
    }
    [ObservableProperty] public partial string EnvironmentVariablesText { get; set; } = "";
    [ObservableProperty] public partial string? DiskUsageLabel { get; set; }
    [ObservableProperty] public partial bool IsLoadingMods { get; set; }

    // ---- Edit sheet: name, icon and banner. A form rather than a live-editing panel, so the
    // three of them commit together and Cancel really does leave the instance alone.

    [ObservableProperty] public partial bool IsEditOpen { get; set; }

    /// <summary>True only while a sheet is sliding back down, so it stays mounted to animate.</summary>
    [ObservableProperty] public partial bool IsEditClosing { get; set; }
    [ObservableProperty] public partial string EditName { get; set; } = "";
    [ObservableProperty] public partial string EditIcon { get; set; } = "🌸";
    [ObservableProperty] public partial string? EditIconImagePath { get; set; }
    [ObservableProperty] public partial string? EditBanner { get; set; }

    /// <summary>An icon is either one of the emoji or a picture; the preview shows whichever.</summary>
    public bool EditIconIsImage => EditIconImagePath is { Length: > 0 };

    partial void OnEditIconImagePathChanged(string? value) => OnPropertyChanged(nameof(EditIconIsImage));

    /// <summary>Set when a picture was chosen during this edit. Both are applied on save.</summary>
    private string? _pendingIconImagePath;
    private string? _pendingBannerImagePath;

    // ---- Per-instance settings sheet. These save as you change them, matching the launcher's
    // own Settings page — a modal with an OK button that silently discarded a slider drag would
    // be the odd one out.

    [ObservableProperty] public partial bool IsInstanceSettingsOpen { get; set; }
    [ObservableProperty] public partial bool IsInstanceSettingsClosing { get; set; }
    [ObservableProperty] public partial bool OverridesMemory { get; set; }
    [ObservableProperty] public partial int InstanceMinMemoryMb { get; set; } = 1024;
    [ObservableProperty] public partial int InstanceMaxMemoryMb { get; set; } = 4096;
    [ObservableProperty] public partial JavaOption? InstanceJava { get; set; }
    [ObservableProperty] public partial bool OverridesJvmArguments { get; set; }
    [ObservableProperty] public partial string InstanceJvmArguments { get; set; } = "";

    /// <summary>What the instance gets when it hasn't asked for anything of its own.</summary>
    [ObservableProperty] public partial string InheritedMemoryLabel { get; set; } = "";

    /// <summary>Suppresses write-back while the sheet is being populated from the instance.</summary>
    private bool _loadingInstanceSettings;

    /// <summary>Scenery behind the hero, picked per instance.</summary>
    [ObservableProperty] public partial Bitmap? Backdrop { get; set; }

    public bool HasNoMods => !IsLoadingMods && Mods.Count == 0;

    /// <summary>No instances exist at all — as opposed to none matching the current search.</summary>
    public bool IsEmpty => _all.Count == 0;
    public bool HasNoMatches => _all.Count > 0 && Items.Count == 0;
    /// <summary>
    /// Kept as its own flag rather than !IsDetailOpen so the library stays on screen underneath
    /// the sheet while it slides up, and only drops out once the slide has finished.
    /// </summary>
    [ObservableProperty] public partial bool IsLibraryVisible { get; set; } = true;

    public bool CanPlay => Selected is not null && !IsBusy && !IsRunning;

    /// <summary>Card play buttons don't depend on selection, only on nothing already running.</summary>
    public bool CanQuickPlay => !IsBusy && !IsRunning;

    /// <summary>Library needs its own progress strip, since launching there shows no page.</summary>
    public bool IsLaunchingFromLibrary => IsBusy || IsRunning;
    /// <summary>
    /// What the button says, which is also the only place the instance's state is stated. There
    /// was a badge under it saying the same thing in more words; two labels for one fact is one
    /// too many, and the button is where the eye already is.
    /// </summary>
    public string PlayLabel => IsRunning ? "Playing" : IsBusy ? "Working…" : "Play";
    public string DeleteQuestion => $"Delete {PendingDelete?.Name}?";
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

            // Same order as the bands themselves, so the toolbar reads down the page.
            var named = _all
                .Select(i => i.Group)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => Rank(g!))
                .ThenBy(g => g, StringComparer.OrdinalIgnoreCase);

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

        RebuildInstanceGroups();
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private void RebuildInstanceGroups()
    {
        var collapsed = _launcher.Settings.CollapsedGroups;

        // Pinned first, then Ungrouped, then named groups alphabetically. Pinned has to lead:
        // sorted as an ordinary name it would land under U for Ungrouped, which is the opposite
        // of what pinning something is for. Order within a band is whatever the toolbar asked
        // for, since Items is already sorted.
        var banded = Items
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Group) ? UngroupedFilter : i.Group!,
                     StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => Rank(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        InstanceGroups.Clear();
        foreach (var group in banded)
        {
            var band = new InstanceGroup(group.Key, !group.Key.Equals(UngroupedFilter, StringComparison.OrdinalIgnoreCase))
            {
                IsExpanded = !collapsed.Contains(group.Key, StringComparer.OrdinalIgnoreCase),
            };

            foreach (var instance in group) band.Items.Add(instance);
            InstanceGroups.Add(band);
        }
    }

    /// <summary>Which band comes first. Pinned, then Ungrouped, then everything named.</summary>
    private static int Rank(string group) =>
        group.Equals(Instance.PinnedGroup, StringComparison.OrdinalIgnoreCase) ? 0
        : group.Equals(UngroupedFilter, StringComparison.OrdinalIgnoreCase) ? 1
        : 2;

    /// <summary>
    /// Empties a group without touching the instances in it — they fall back to Ungrouped. A
    /// group is only a label, so deleting one should never be able to cost anyone a world.
    /// </summary>
    [RelayCommand]
    private void DeleteGroup(InstanceGroup? group)
    {
        if (group is not { CanDelete: true }) return;

        foreach (var instance in _all.Where(i =>
            string.Equals(i.Group, group.Name, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            instance.Group = null;
            _launcher.Instances.Save(instance);
        }

        _launcher.Settings.CollapsedGroups.RemoveAll(n => n.Equals(group.Name, StringComparison.OrdinalIgnoreCase));
        _launcher.SaveSettings();

        RefreshGroups();
        RefreshAssignableGroups();
        ApplyFilter();
    }

    /// <summary>Matches the band animation in Asobu.axaml; keep the two in step.</summary>
    private const int BandFoldMilliseconds = 180;

    [RelayCommand]
    private async Task ToggleGroupAsync(InstanceGroup? group)
    {
        if (group is null || group.IsCollapsing) return;

        if (group.IsExpanded)
        {
            // Stays mounted and stays .open for the length of the fold — dropping IsExpanded
            // straight away would unmount the cards before a frame of the exit had drawn.
            group.IsCollapsing = true;
            await Task.Delay(BandFoldMilliseconds);
            group.IsCollapsing = false;
            group.IsExpanded = false;
        }
        else
        {
            group.IsExpanded = true;
        }

        // Persisted, so a library with a dozen bands doesn't reopen fully expanded every launch.
        var collapsed = _launcher.Settings.CollapsedGroups;
        if (group.IsExpanded)
            collapsed.RemoveAll(name => name.Equals(group.Name, StringComparison.OrdinalIgnoreCase));
        else if (!collapsed.Contains(group.Name, StringComparer.OrdinalIgnoreCase))
            collapsed.Add(group.Name);

        _launcher.SaveSettings();
    }

    partial void OnSelectedChanged(Instance? value)
    {
        DismissSheets();
        EnvironmentVariablesText = value is null ? "" : FormatEnvironment(value.EnvironmentVariables);
        DiskUsageLabel = null;
        Backdrop = Backdrops.For(value, _launcher.Paths);

        LoadInstanceSettings(value);
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(SettingsSummary));

        if (value is { } instance) _ = LoadDiskUsageAsync(instance);
    }

    partial void OnIsDetailOpenChanged(bool value)
    {
        if (!value) Error = null;
    }

    private async Task LoadDiskUsageAsync(Instance instance)
    {
        var path = _launcher.Paths.InstanceDir(instance.Folder);
        var size = await Task.Run(() => DirectorySize.Compute(path));

        if (Selected?.Id == instance.Id) DiskUsageLabel = Format.Bytes(size);
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayLabel));
        OnPropertyChanged(nameof(CanQuickPlay));
        OnPropertyChanged(nameof(IsLaunchingFromLibrary));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayLabel));
        OnPropertyChanged(nameof(CanQuickPlay));
        OnPropertyChanged(nameof(IsLaunchingFromLibrary));
    }

    partial void OnPendingDeleteChanged(Instance? value) => OnPropertyChanged(nameof(DeleteQuestion));

    public void RefreshAccountLabel() => OnPropertyChanged(nameof(AccountLabel));

    // ---- Navigation ----

    [RelayCommand]
    private void OpenInstance(Instance? instance)
    {
        if (instance is null) return;
        Selected = instance;
        IsDetailOpen = true;
        _ = LoadModsAsync(instance);
        _ = HideLibraryAfterSlideAsync();
    }

    /// <summary>Matches the sheet's slide duration in Asobu.axaml; keep the two in step.</summary>
    private const int SheetSlideMilliseconds = 340;

    private async Task HideLibraryAfterSlideAsync()
    {
        await Task.Delay(SheetSlideMilliseconds);

        // Guard against the sheet having been closed again mid-slide.
        if (IsDetailOpen) IsLibraryVisible = false;
    }

    /// <summary>
    /// Which load is the current one. Opening an instance, switching the dropdown and pressing
    /// Rescan can all be in flight together, and a scan that finished after a newer one started
    /// must not write to the list — appending to a list another load already filled is how every
    /// row ends up on screen twice.
    /// </summary>
    private int _modsLoad;

    /// <summary>Reads the instance's content folder off the UI thread — each jar is a zip open.</summary>
    private async Task LoadModsAsync(Instance instance)
    {
        var load = ++_modsLoad;

        IsLoadingMods = true;

        try
        {
            var kind = Kind;

            // Everything reads all five folders and shows them together. Each row keeps its own
            // folder's rules, which is why the toggle is decided per row rather than per list.
            var kinds = kind == ModKind.Any ? ModContent.Local : [kind];
            var folder = instance.Folder;

            var found = await Task.Run(() => kinds
                .Select(one => (Kind: one, Directory: ModScanner.ContentDirectory(_launcher.Paths, folder, one)))
                .Where(pair => pair.Directory is not null)
                .SelectMany(pair => ModScanner.Scan(pair.Directory!, pair.Kind)
                    .Select(entry => (Entry: entry, pair.Kind)))
                .ToList());

            // Superseded, or the dropdown moved on while a large folder was being read.
            if (load != _modsLoad || Selected?.Id != instance.Id || Kind != kind) return;

            // Emptied here rather than before the scan: the list stays as it was until there is
            // something to replace it with, so a rescan does not blink the table away.
            Mods.Clear();
            foreach (var (entry, entryKind) in found)
                Mods.Add(new ModRowViewModel(entry) { CanToggle = entryKind != ModKind.World });

            // After the folder is on screen, not before: hashing every jar and asking two web
            // services about them is not something to keep a list waiting for.
            if (ChecksUpdates) _ = CheckForUpdatesAsync(instance, [.. found.Select(pair => pair.Entry)]);
        }
        finally
        {
            if (load == _modsLoad)
            {
                IsLoadingMods = false;
                OnPropertyChanged(nameof(HasNoMods));
                OnPropertyChanged(nameof(ContentCountLabel));

                // The automatic heap size is read off the mod count, which is only now known.
                if (Selected?.Id == instance.Id) InheritedMemoryLabel = DescribeInheritedMemory(instance);
            }
        }
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        if (Selected is not { } instance) return;

        var directory = ModScanner.ContentDirectory(_launcher.Paths, instance.Folder, Kind)
            ?? ModScanner.ModsDirectory(_launcher.Paths, instance.Folder);

        // Created on the way: a folder nothing has been installed into yet does not exist, and
        // "Open folder" doing nothing at all reads as a broken button.
        System.IO.Directory.CreateDirectory(directory);
        AsobuLauncher.OpenFolder(directory);
    }

    [RelayCommand]
    private void RefreshMods()
    {
        if (Selected is { } instance) _ = LoadModsAsync(instance);
    }

    [RelayCommand]
    private async Task CloseDetailAsync()
    {
        if (IsDetailClosing) return;

        // Any open modal goes with the sheet, so reopening the instance doesn't come back to it.
        DismissSheets();

        // Library comes back first so the sheet slides down onto it rather than onto a blank page.
        IsLibraryVisible = true;
        Reload();

        // Stay mounted for the length of the slide, then unmount — flipping IsDetailOpen straight
        // away would collapse the sheet before a single frame of the animation had drawn.
        IsDetailClosing = true;
        await Task.Delay(SheetSlideMilliseconds);
        IsDetailClosing = false;
        IsDetailOpen = false;
    }

    [RelayCommand]
    private void NewInstance()
    {
        // The doorway first: from scratch, or bring something in from elsewhere.
        IsFlowGoingBack = false;
        IsFlowChooseVisible = true;
        IsFlowChooseLeaving = false;
        IsFlowImportVisible = false;
        IsFlowImportLeaving = false;
        IsFlowWorkingVisible = false;
        IsFlowWorkingLeaving = false;
        IsImportDone = false;
        ImportError = null;
        ImportCode = "";
        ImportNotes.Clear();
        _imported = null;

        IsNewFlowOpen = true;
    }

    // ---- The new-instance doorway. One card, three panes — choose, import, working — that
    // slide between each other like pages instead of the card blinking its content out. ----

    /// <summary>Matches the pane animation in Asobu.axaml; keep the two in step.</summary>
    private const int PaneSlideMilliseconds = 200;

    [ObservableProperty] public partial bool IsNewFlowOpen { get; set; }
    [ObservableProperty] public partial bool IsNewFlowClosing { get; set; }

    /// <summary>
    /// Which way the next slide goes. One flag rather than one per pane: only one pair of panes
    /// ever moves at a time, and backing up should read as backing up on both of them.
    /// </summary>
    [ObservableProperty] public partial bool IsFlowGoingBack { get; set; }

    [ObservableProperty] public partial bool IsFlowChooseVisible { get; set; } = true;
    [ObservableProperty] public partial bool IsFlowChooseLeaving { get; set; }
    [ObservableProperty] public partial bool IsFlowImportVisible { get; set; }
    [ObservableProperty] public partial bool IsFlowImportLeaving { get; set; }
    [ObservableProperty] public partial bool IsFlowWorkingVisible { get; set; }
    [ObservableProperty] public partial bool IsFlowWorkingLeaving { get; set; }
    [ObservableProperty] public partial bool IsFlowBlockedVisible { get; set; }
    [ObservableProperty] public partial bool IsFlowBlockedLeaving { get; set; }

    [ObservableProperty] public partial string ImportCode { get; set; } = "";
    [ObservableProperty] public partial string ImportStatus { get; set; } = "";
    [ObservableProperty] public partial double ImportFraction { get; set; }
    [ObservableProperty] public partial string? ImportError { get; set; }
    [ObservableProperty] public partial bool IsImporting { get; set; }

    /// <summary>True once an import finished but left notes worth reading before moving on.</summary>
    [ObservableProperty] public partial bool IsImportDone { get; set; }

    public ObservableCollection<string> ImportNotes { get; } = [];

    private CancellationTokenSource? _importCts;
    private Instance? _imported;

    // ---- Files only their author's page may serve. The launcher cannot fetch these, so it
    // sends the person to the page and watches for what they download. ----

    public ObservableCollection<BlockedDownloadRow> BlockedDownloads { get; } = [];

    private CancellationTokenSource? _watchCts;

    public bool HasBlockedDownloads => BlockedDownloads.Count > 0;

    /// <summary>How many are still outstanding, for the line above the list.</summary>
    public string BlockedSummary
    {
        get
        {
            var left = BlockedDownloads.Count(row => !row.IsDone);

            return left == 0
                ? "All of them are in. The instance is ready."
                : left == 1
                    ? "One mod can only be downloaded from its own page."
                    : $"{left} mods can only be downloaded from their own pages.";
        }
    }

    public bool IsBlockedFinished => BlockedDownloads.Count > 0 && BlockedDownloads.All(row => row.IsDone);

    [RelayCommand]
    private async Task CloseNewFlowAsync()
    {
        // A running import keeps the door shut: closing the card would orphan it mid-download.
        if (IsImporting) return;
        if (!IsNewFlowOpen || IsNewFlowClosing) return;

        StopWatching();

        IsNewFlowClosing = true;
        await Task.Delay(ModalSlideMilliseconds);
        IsNewFlowClosing = false;
        IsNewFlowOpen = false;
    }

    private void StopWatching()
    {
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        _watchCts = null;
    }

    [RelayCommand]
    private async Task FlowCreateAsync()
    {
        await CloseNewFlowAsync();
        _requestNewInstance();
    }

    [RelayCommand]
    private async Task FlowToImportAsync()
    {
        IsFlowGoingBack = false;
        IsFlowChooseLeaving = true;
        await Task.Delay(PaneSlideMilliseconds);
        IsFlowChooseLeaving = false;
        IsFlowChooseVisible = false;
        IsFlowImportVisible = true;
    }

    [RelayCommand]
    private async Task FlowBackAsync()
    {
        if (IsImporting) return;

        IsFlowGoingBack = true;

        if (IsFlowWorkingVisible)
        {
            IsFlowWorkingLeaving = true;
            await Task.Delay(PaneSlideMilliseconds);
            IsFlowWorkingLeaving = false;
            IsFlowWorkingVisible = false;
            ImportError = null;
            IsImportDone = false;
            ImportNotes.Clear();
            IsFlowImportVisible = true;
            return;
        }

        IsFlowImportLeaving = true;
        await Task.Delay(PaneSlideMilliseconds);
        IsFlowImportLeaving = false;
        IsFlowImportVisible = false;
        IsFlowChooseVisible = true;
    }

    [RelayCommand]
    private Task RunCodeImportAsync() =>
        ImportCode.Trim().Length == 0
            ? Task.CompletedTask
            : RunImportAsync(token => _launcher.Importer.ImportCodeAsync(ImportCode, ImportSink(), token));

    /// <summary>Called by the view once its file picker has an answer.</summary>
    public Task ImportFromFileAsync(string path) =>
        RunImportAsync(token => _launcher.Importer.ImportFileAsync(path, ImportSink(), token));

    public Task ImportFromFolderAsync(string path) =>
        RunImportAsync(token => _launcher.Importer.ImportFolderAsync(path, ImportSink(), token));

    [RelayCommand]
    private void CancelImport() => _importCts?.Cancel();

    private IProgress<InstallProgress> ImportSink() => new Progress<InstallProgress>(p =>
    {
        ImportStatus = p.Stage;
        ImportFraction = p.Fraction;
    });

    private async Task RunImportAsync(Func<CancellationToken, Task<ImportOutcome>> run)
    {
        if (IsImporting) return;

        // Slide to the working pane, wherever the request came from.
        IsFlowGoingBack = false;
        if (IsFlowImportVisible)
        {
            IsFlowImportLeaving = true;
            await Task.Delay(PaneSlideMilliseconds);
            IsFlowImportLeaving = false;
            IsFlowImportVisible = false;
        }

        ImportError = null;
        IsImportDone = false;
        ImportNotes.Clear();
        ImportStatus = "Starting";
        ImportFraction = 0;
        IsFlowWorkingVisible = true;
        IsImporting = true;

        _importCts = new CancellationTokenSource();
        ImportOutcome outcome;
        try
        {
            outcome = await run(_importCts.Token);
        }
        catch (OperationCanceledException)
        {
            IsImporting = false;
            await FlowBackAsync();
            return;
        }
        catch (Exception e)
        {
            // The importer speaks in reasons; anything that escapes it is still shown, not lost.
            IsImporting = false;
            ImportError = e.Message;
            return;
        }
        finally
        {
            _importCts.Dispose();
            _importCts = null;
        }

        IsImporting = false;

        if (!outcome.Succeeded)
        {
            ImportError = outcome.Reason;
            return;
        }

        Reload();
        _imported = outcome.Instance;

        // Files the launcher is not allowed to fetch come with their own pane: it lists them,
        // sends the person to each author's page, and files away what lands.
        if (outcome.Blocked.Count > 0)
        {
            await ShowBlockedAsync(outcome.Blocked, outcome.Notes);
            return;
        }

        // Anything worth knowing stays on screen until it has been seen; a clean import just goes.
        if (outcome.Notes.Count > 0)
        {
            foreach (var note in outcome.Notes) ImportNotes.Add(note);
            ImportStatus = "Imported";
            ImportFraction = 1;
            IsImportDone = true;
            return;
        }

        await FlowOpenImportedAsync();
    }

    private async Task ShowBlockedAsync(IReadOnlyList<BlockedDownload> blocked, IReadOnlyList<string> notes)
    {
        BlockedDownloads.Clear();
        foreach (var item in blocked) BlockedDownloads.Add(new BlockedDownloadRow(item));

        ImportNotes.Clear();
        foreach (var note in notes) ImportNotes.Add(note);

        OnPropertyChanged(nameof(HasBlockedDownloads));
        OnPropertyChanged(nameof(BlockedSummary));
        OnPropertyChanged(nameof(IsBlockedFinished));

        IsFlowGoingBack = false;
        IsFlowWorkingLeaving = true;
        await Task.Delay(PaneSlideMilliseconds);
        IsFlowWorkingLeaving = false;
        IsFlowWorkingVisible = false;
        IsFlowBlockedVisible = true;

        StartWatching();
    }

    /// <summary>
    /// Watches the download folders for everything still outstanding. Started as soon as the
    /// list is up rather than when a page is opened: someone may already have the jar sitting in
    /// Downloads from an earlier attempt, and that should just be taken.
    /// </summary>
    private void StartWatching()
    {
        StopWatching();

        var pending = BlockedDownloads.Where(row => !row.IsDone).Select(row => row.Item).ToList();
        if (pending.Count == 0) return;

        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;

        _ = new ManualDownloadWatcher().RunAsync(
            pending,
            item => Dispatcher.UIThread.Post(() => MarkLanded(item)),
            token);
    }

    private void MarkLanded(BlockedDownload item)
    {
        foreach (var row in BlockedDownloads.Where(row => ReferenceEquals(row.Item, item)))
            row.IsDone = true;

        OnPropertyChanged(nameof(BlockedSummary));
        OnPropertyChanged(nameof(IsBlockedFinished));

        if (IsBlockedFinished) StopWatching();
    }

    [RelayCommand]
    private void OpenBlockedPage(BlockedDownloadRow? row)
    {
        if (row is null) return;

        row.IsWaiting = true;
        AsobuLauncher.OpenUrl(row.Item.PageUrl);
    }

    /// <summary>Opens every outstanding page at once, for a pack that blocks more than one.</summary>
    [RelayCommand]
    private void OpenAllBlockedPages()
    {
        foreach (var row in BlockedDownloads.Where(row => !row.IsDone))
        {
            row.IsWaiting = true;
            AsobuLauncher.OpenUrl(row.Item.PageUrl);
        }
    }

    /// <summary>Called by the view when someone points at a file the watcher never saw.</summary>
    public void AcceptChosenFile(BlockedDownloadRow row, string path)
    {
        if (!ManualDownloadWatcher.TryAcceptChosen(row.Item, path)) return;

        row.IsDone = true;
        OnPropertyChanged(nameof(BlockedSummary));
        OnPropertyChanged(nameof(IsBlockedFinished));

        if (IsBlockedFinished) StopWatching();
    }

    [RelayCommand]
    private async Task FinishBlockedAsync()
    {
        StopWatching();
        await FlowOpenImportedAsync();
    }

    [RelayCommand]
    private async Task FlowOpenImportedAsync()
    {
        await CloseNewFlowAsync();

        if (_imported is { } imported)
            Selected = _all.FirstOrDefault(i => i.Id == imported.Id) ?? imported;
    }

    /// <summary>
    /// Opens the catalogue already knowing where anything found is going. The sheet stays
    /// mounted underneath, so closing the browser lands back on this instance's page — and the
    /// mods list is re-read on the way, since that is the whole point of having been there.
    /// </summary>
    [RelayCommand]
    private void AddMods()
    {
        // Opened on whatever the card is listing: asked to add shaders, the browser should
        // already be showing shaders.
        if (Selected is { } instance) _requestAddMods(instance, Kind);
    }

    // ---- Adding content: download it, or bring one you already have. The same two doors the
    // new-instance modal offers, worded for whichever kind the card is listing.

    [ObservableProperty] public partial bool IsAddContentOpen { get; set; }
    [ObservableProperty] public partial bool IsAddContentClosing { get; set; }

    /// <summary>What was just added by hand, or why something was not. Cleared on each visit.</summary>
    public ObservableCollection<string> AddContentNotes { get; } = [];

    public bool HasAddContentNotes => AddContentNotes.Count > 0;

    /// <summary>"a mod", "a resource pack" — the noun with its article, since every line needs it.</summary>
    private string One => Kind switch
    {
        ModKind.Any => "something",
        ModKind.ResourcePack => "a resource pack",
        ModKind.Shader => "a shader pack",
        ModKind.DataPack => "a data pack",
        ModKind.World => "a world",
        _ => "a mod",
    };

    public string AddDownloadTitle => $"Download {One}";

    public string AddDownloadDetail => Kind == ModKind.World
        ? "Browse Modrinth and CurseForge for a world to drop into this instance."
        : $"Browse Modrinth and CurseForge for {One} that works with this instance.";

    public string AddLocalTitle => $"Or add {One} you have";

    public string AddLocalDetail => Kind switch
    {
        ModKind.Any =>
            "Choose anything — a mod, a pack, a world — and Asobu reads what it is and files it in the right folder.",
        ModKind.Mod => "Choose a .jar from your computer and Asobu copies it into the instance.",
        ModKind.World =>
            "Choose a world folder or a .zip. It is copied in and listed here as one Asobu added — your own worlds stay where they are.",
        _ => "Choose a .zip from your computer and Asobu copies it into the instance. An unzipped folder works too.",
    };

    /// <summary>What the file picker should offer, which the view asks for by name.</summary>
    public string LocalFilePattern => Kind switch
    {
        ModKind.Mod => "*.jar",
        ModKind.Any => "*.jar",
        _ => "*.zip",
    };

    /// <summary>A second pattern, so Everything can offer both without two pickers.</summary>
    public bool AcceptsAnyFile => Kind == ModKind.Any;

    /// <summary>
    /// Whether picking a folder makes sense. It does for a world, and for a pack someone keeps
    /// unzipped; it never does for a mod, which the loader will only read as a .jar.
    /// </summary>
    public bool CanAddFolder => Kind != ModKind.Mod;

    [RelayCommand]
    private void OpenAddContent()
    {
        if (Selected is null) return;

        AddContentNotes.Clear();
        OnPropertyChanged(nameof(HasAddContentNotes));

        OnPropertyChanged(nameof(AddDownloadTitle));
        OnPropertyChanged(nameof(AddDownloadDetail));
        OnPropertyChanged(nameof(AddLocalTitle));
        OnPropertyChanged(nameof(AddLocalDetail));
        OnPropertyChanged(nameof(CanAddFolder));

        IsAddContentClosing = false;
        IsAddContentOpen = true;
    }

    [RelayCommand]
    private async Task CloseAddContentAsync()
    {
        if (!IsAddContentOpen || IsAddContentClosing) return;

        IsAddContentClosing = true;
        await Task.Delay(ModalSlideMilliseconds);
        IsAddContentClosing = false;
        IsAddContentOpen = false;
    }

    /// <summary>The catalogue half: closes the sheet and opens the browser, already scoped.</summary>
    [RelayCommand]
    private async Task AddContentFromCatalogueAsync()
    {
        await CloseAddContentAsync();
        AddMods();
    }

    /// <summary>
    /// The other half, once the view has picked the files. Whatever landed is reported rather
    /// than assumed: a file that could not be used is exactly what someone needs told.
    /// </summary>
    public async Task AddLocalContentAsync(IReadOnlyList<string> paths)
    {
        if (Selected is not { } instance || paths.Count == 0) return;

        var kind = Kind;
        var result = await Task.Run(() => _launcher.AddLocalContent(instance, kind, paths));

        AddContentNotes.Clear();

        if (result.Added.Count > 0)
            AddContentNotes.Add(result.Added.Count == 1
                ? $"Added {result.Added[0]}."
                : $"Added {result.Added.Count} files.");

        foreach (var skipped in result.Skipped) AddContentNotes.Add($"Couldn't add {skipped}.");

        OnPropertyChanged(nameof(HasAddContentNotes));

        await LoadModsAsync(instance);

        // Nothing to read means nothing to stop for.
        if (result.Skipped.Count == 0) await CloseAddContentAsync();
    }

    /// <summary>Called when the instance's browser closes, so anything added shows up.</summary>
    public void RefreshModsAfterBrowsing()
    {
        if (Selected is { } instance) _ = LoadModsAsync(instance);
    }

    [RelayCommand]
    private void ViewCrashReports()
    {
        if (Selected is not { } instance) return;

        // The sheet that launched this stays mounted while the crash page is up, so coming back
        // would land on a modal the user already left. Close it on the way out.
        DismissSheets();
        _requestCrashReports(instance);
    }

    /// <summary>
    /// The running game's output as it arrives. The archive — every past session, plus whatever
    /// crash reports Minecraft wrote itself — stays on the crash reports page, which is a
    /// different question from "what is it doing right now".
    /// </summary>
    [RelayCommand]
    private void ViewLogs() => ViewLiveLog();

    // ---- Edit sheet: name, icon, banner ----

    [RelayCommand]
    private void OpenEdit()
    {
        if (Selected is not { } instance) return;

        EditName = instance.Name;
        EditIcon = instance.Icon;
        EditIconImagePath = instance.IconImagePath;
        EditBanner = instance.Banner;
        _pendingIconImagePath = null;
        _pendingBannerImagePath = null;

        RebuildBannerChoices(instance);
        DismissSheets();
        IsEditOpen = true;
    }

    [RelayCommand]
    private async Task CancelEditAsync()
    {
        _pendingIconImagePath = null;
        _pendingBannerImagePath = null;
        await CloseEditAsync();
    }

    /// <summary>Matches the modal animation in Asobu.axaml; keep the two in step.</summary>
    private const int ModalSlideMilliseconds = 240;

    /// <summary>
    /// Slides a sheet away rather than cutting it. It has to stay mounted and stay .open for the
    /// length of the animation — flipping the open flag straight away would collapse it before a
    /// single frame had drawn, which is exactly what the instant dismissals below want.
    /// </summary>
    private async Task CloseEditAsync()
    {
        if (!IsEditOpen || IsEditClosing) return;

        IsEditClosing = true;
        await Task.Delay(ModalSlideMilliseconds);
        IsEditClosing = false;
        IsEditOpen = false;
    }

    private async Task CloseSettingsSheetAsync()
    {
        if (!IsInstanceSettingsOpen || IsInstanceSettingsClosing) return;

        IsInstanceSettingsClosing = true;
        await Task.Delay(ModalSlideMilliseconds);
        IsInstanceSettingsClosing = false;
        IsInstanceSettingsOpen = false;
    }

    /// <summary>
    /// Drops any open sheet on the spot, for the cases where the page underneath it is going
    /// away too — deleting the instance, leaving for the crash log, closing the whole thing.
    /// Animating an exit onto a surface that is itself mid-exit just reads as a glitch.
    /// </summary>
    private void DismissSheets()
    {
        // Settings taken away rather than finished with — opening another sheet, or moving to a
        // different instance. The loader change stands; the question about its mods does not
        // follow the person around to be asked later out of context.
        _pendingMoveLoader = null;

        IsEditOpen = false;
        IsEditClosing = false;
        IsInstanceSettingsOpen = false;
        IsInstanceSettingsClosing = false;

        // The log follows the rest. Its timer goes with it rather than ticking behind a sheet
        // that is no longer on screen.
        _logTimer?.Stop();
        IsLogOpen = false;
        IsLogClosing = false;
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (Selected is not { } instance) return;

        // Through the store rather than set here: a rename moves the instance's folder, and
        // the folder is the thing someone goes looking for outside the launcher.
        var name = EditName.Trim();
        if (name.Length > 0) _launcher.Instances.Rename(instance, name);

        // The copies have to happen before Save: each one is what decides its own token.
        if (_pendingIconImagePath is { } iconPath)
        {
            TryArtwork(() => _launcher.Instances.SetCustomIcon(instance, iconPath));
        }
        else
        {
            // Going back to an emoji leaves the old picture behind otherwise, invisible but
            // still riding along in every export of this instance.
            if (instance.HasCustomIcon && EditIcon != instance.Icon)
                _launcher.Instances.ClearCustomIcon(instance);

            instance.Icon = EditIcon;
        }

        if (_pendingBannerImagePath is { } bannerPath)
        {
            TryArtwork(() => _launcher.Instances.SetCustomBanner(instance, bannerPath));
        }
        else
        {
            instance.Banner = EditBanner;
        }

        _launcher.Instances.Save(instance);

        _pendingIconImagePath = null;
        _pendingBannerImagePath = null;

        // Apply first, slide out after: the sheet spends its exit showing the saved state
        // rather than snapping to it once the animation is over.
        Backdrop = Backdrops.For(instance, _launcher.Paths);
        ApplyFilter();
        OnPropertyChanged(nameof(Selected));

        await CloseEditAsync();
    }

    [RelayCommand]
    private void SelectEditIcon(string? icon)
    {
        if (icon is null) return;

        EditIcon = icon;
        EditIconImagePath = null;
        _pendingIconImagePath = null;
    }

    /// <summary>
    /// Called from the view, which owns the file dialog. Nothing is copied into the instance
    /// until Save, so backing out of the edit leaves nothing behind on disk.
    /// </summary>
    public void StageCustomIcon(string imagePath)
    {
        _pendingIconImagePath = imagePath;
        EditIcon = Instance.CustomIconPrefix + Path.GetFileName(imagePath);
        EditIconImagePath = imagePath;
    }

    private void TryArtwork(Action apply)
    {
        try
        {
            apply();
        }
        catch (Exception ex)
        {
            Error = $"Couldn't use that image: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectEditBanner(BannerChoice? choice)
    {
        if (choice is null) return;

        EditBanner = choice.Token;
        _pendingBannerImagePath = null;
        MarkSelectedBanner();
    }

    /// <summary>
    /// Called from the view, which owns the file dialog. The image isn't copied into the
    /// instance until Save, so backing out of the edit leaves nothing behind on disk.
    /// </summary>
    public void StageCustomBanner(string imagePath)
    {
        _pendingBannerImagePath = imagePath;
        EditBanner = Instance.CustomBannerPrefix + Path.GetFileName(imagePath);

        var preview = new BannerChoice(EditBanner, "Your picture", LoadPreview(imagePath));

        // Replace rather than append, so repicking doesn't stack up dead thumbnails.
        for (var i = BannerChoices.Count - 1; i >= 0; i--)
            if (BannerChoices[i].Token?.StartsWith(Instance.CustomBannerPrefix, StringComparison.Ordinal) == true)
                BannerChoices.RemoveAt(i);

        BannerChoices.Add(preview);
        MarkSelectedBanner();
    }

    private void RebuildBannerChoices(Instance instance)
    {
        BannerChoices.Clear();
        BannerChoices.Add(new BannerChoice(null, "Surprise me", Backdrops.ForInstance(instance.Id)));

        foreach (var file in Backdrops.BuiltIn)
            BannerChoices.Add(new BannerChoice(
                Instance.BuiltInBannerPrefix + file, "Scenery", Backdrops.LoadBuiltIn(file)));

        if (instance.Banner is { } banner
            && banner.StartsWith(Instance.CustomBannerPrefix, StringComparison.Ordinal))
        {
            BannerChoices.Add(new BannerChoice(
                banner, "Your picture", Backdrops.For(instance, _launcher.Paths)));
        }

        MarkSelectedBanner();
    }

    private void MarkSelectedBanner()
    {
        foreach (var choice in BannerChoices)
            choice.IsSelected = choice.Token == EditBanner;
    }

    private static Bitmap? LoadPreview(string path)
    {
        try
        {
            return new Bitmap(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- Group ----

    private void RefreshAssignableGroups()
    {
        AssignableGroups.Clear();
        AssignableGroups.Add(UngroupedFilter);

        // Selected's own group is appended in case it was just created and hasn't reached the
        // library list yet; Distinct keeps that from showing up twice.
        //
        // Pinned is left off. It is a real group and belongs in the library's bands and its
        // filter, but as somewhere to assign an instance it would be a second, worse way to do
        // what the context menu already does in one click — and an instance assigned to it from
        // here would be pinned without the word ever appearing.
        foreach (var name in _all
            .Select(i => i.Group)
            .Append(Selected?.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Where(g => !Instance.PinnedGroup.Equals(g, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
        {
            AssignableGroups.Add(name!);
        }
    }

    partial void OnSelectedGroupChanged(string? value)
    {
        if (_loadingInstanceSettings || Selected is not { } instance) return;

        var group = value is null or UngroupedFilter ? null : value;
        if (group == instance.Group) return;

        instance.Group = group;
        _launcher.Instances.Save(instance);
        RefreshGroups();
        ApplyFilter();
    }

    [RelayCommand]
    private void StartNewGroup()
    {
        NewGroupName = "";
        IsNewGroupOpen = true;
    }

    [RelayCommand]
    private void CancelNewGroup() => IsNewGroupOpen = false;

    [RelayCommand]
    private void CreateGroup()
    {
        // Checked here as well as on the button. The button being disabled is a courtesy to
        // whoever is looking at it; this is the rule, and it is the one a command invoked any
        // other way still has to pass.
        if (!CanCreateGroup) return;

        var name = NewGroupName.Trim();

        // Re-use a group that already exists under different casing rather than creating a near
        // duplicate — and the ComboBox needs the exact instance it holds to show a selection.
        var existing = AssignableGroups.FirstOrDefault(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            AssignableGroups.Add(name);
            existing = name;
        }

        IsNewGroupOpen = false;
        SelectedGroup = existing;
    }

    // ---- Per-instance settings ----

    [RelayCommand]
    private void OpenInstanceSettings()
    {
        if (Selected is null) return;

        RefreshJavaOptions();
        LoadInstanceSettings(Selected);

        // Asked while the sheet opens rather than before it: four services have to answer, and
        // waiting on them would put a pause between the click and the sheet appearing.
        _ = LoadLoaderChoicesAsync(Selected);

        DismissSheets();
        IsInstanceSettingsOpen = true;
    }

    /// <summary>
    /// Leaves the sheet without applying the loader. Everything else here saved as it was
    /// changed, so this cancels the one thing that had not — which is why it sits beside Done
    /// rather than pretending to undo the whole sheet.
    /// </summary>
    [RelayCommand]
    private async Task CancelInstanceSettingsAsync()
    {
        _pendingMoveLoader = null;

        if (Selected is { } instance)
        {
            _loadingLoaderChoices = true;
            SelectedLoader = instance.LoaderName;
            _loadingLoaderChoices = false;
        }

        await CloseSettingsSheetAsync();
    }

    [RelayCommand]
    private async Task CloseInstanceSettingsAsync()
    {
        // Whatever the picker ended on, which is not necessarily the first thing it was set
        // to — someone may try Forge, read what it says, and settle on NeoForge.
        var chosen = _pendingMoveLoader;
        _pendingMoveLoader = null;

        if (Selected is not { } instance || chosen is not { Length: > 0 })
        {
            await CloseSettingsSheetAsync();
            return;
        }

        // Applied while the sheet is still up, so a refusal — no build of that loader for this
        // Minecraft version — is read against the picker that caused it.
        IsCheckingLoaderChange = true;

        string? applied;
        try
        {
            applied = await ApplyLoaderChoiceAsync(instance, chosen);
        }
        finally
        {
            IsCheckingLoaderChange = false;
        }

        await CloseSettingsSheetAsync();

        if (applied is { Length: > 0 }) await AskAboutMovingModsAsync(applied);
    }

    /// <summary>
    /// Works out what would become of each mod on the new loader, then puts the list up. Done
    /// after the settings sheet has gone, so the answer arrives on a clear screen.
    /// </summary>
    private async Task AskAboutMovingModsAsync(string loader)
    {
        if (Selected is not { } instance) return;

        IsCheckingLoaderChange = true;

        try
        {
            // Usually already finished, since it started when the picker moved. Where the
            // prefetch is still running, this waits for it — the download is happening either
            // way, and a moment here buys an instant Move afterwards.
            var plan = _movePlan is { } running
                       && string.Equals(_movePlanFor, loader, StringComparison.OrdinalIgnoreCase)
                ? await running
                : await _launcher.PlanLoaderMoveAsync(instance, loader);

            if (plan.Count == 0 || Selected?.Id != instance.Id) return;

            Moves.Clear();
            foreach (var move in plan) Moves.Add(new MoveRow(move));

            OnPropertyChanged(nameof(MoveQuestion));
            OnPropertyChanged(nameof(HasStuckMods));
            OnPropertyChanged(nameof(NothingCanMove));
            OnPropertyChanged(nameof(StuckSummary));
            OnPropertyChanged(nameof(RevertLabel));

            IsMovePromptClosing = false;
            IsMovePromptOpen = true;
        }
        catch (OperationCanceledException)
        {
            // The picker moved again before Done. The plan for whatever it landed on is the one
            // that matters, and this is not it.
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            _movePlan = null;
            _movePlanFor = null;
            IsCheckingLoaderChange = false;
        }
    }

    [RelayCommand]
    private void RefreshJavaOptions()
    {
        var current = InstanceJava?.Value;

        JavaOptions.Clear();
        JavaOptions.Add(new JavaOption(InheritJava, "Same as the launcher",
            DescribeGlobalJava()));
        JavaOptions.Add(new JavaOption("auto", "Automatic",
            "Download the exact runtime this Minecraft version asks for."));

        foreach (var installation in JavaManager.DetectSystemJava())
            JavaOptions.Add(new JavaOption(installation.ExecutablePath, $"Java {installation.Major}", installation.Source));

        // Keep a path that was set by hand visible rather than silently dropping the instance
        // back to inheriting when its runtime isn't one we detected.
        if (Selected?.JavaSelection is { Length: > 0 } configured
            && configured is not "auto"
            && JavaOptions.All(o => o.Value != configured))
        {
            JavaOptions.Add(new JavaOption(configured, "Custom", configured));
        }

        InstanceJava = JavaOptions.FirstOrDefault(o => o.Value == current) ?? JavaOptions[0];
    }

    private int InheritedMaxMemoryMb(Instance instance) =>
        _launcher.Settings.AutomaticMemory
            ? MemoryPlanner.MaxMemoryMbFor(_launcher.Paths, instance)
            : _launcher.Settings.MaxMemoryMb;

    private string DescribeInheritedMemory(Instance instance)
    {
        if (!_launcher.Settings.AutomaticMemory)
            return $"Launcher default: {_launcher.Settings.MaxMemoryMb} MB.";

        var mods = MemoryPlanner.CountMods(_launcher.Paths, instance);
        var reason = mods == 0 ? "no mods" : mods == 1 ? "1 mod" : $"{mods} mods";

        return $"Automatic: {MemoryPlanner.MaxMemoryMbFor(_launcher.Paths, instance)} MB, sized to {reason}.";
    }

    private string DescribeGlobalJava() =>
        _launcher.Settings.UsesManagedJava
            ? "Currently: automatic"
            : $"Currently: {_launcher.Settings.JavaSelection}";

    private void LoadInstanceSettings(Instance? instance)
    {
        _loadingInstanceSettings = true;
        try
        {
            var global = _launcher.Settings;

            OverridesMemory = instance?.MaxMemoryMb is not null || instance?.MinMemoryMb is not null;

            // Sliders start from whatever the instance would get anyway, so turning the toggle
            // on is a nudge from a sensible figure rather than a jump to an arbitrary one.
            var inherited = instance is null ? global.MaxMemoryMb : InheritedMaxMemoryMb(instance);
            InstanceMinMemoryMb = instance?.MinMemoryMb ?? MemoryPlanner.MinMemoryMbFor(inherited);
            InstanceMaxMemoryMb = instance?.MaxMemoryMb ?? inherited;
            InheritedMemoryLabel = instance is null ? "" : DescribeInheritedMemory(instance);

            OverridesJvmArguments = instance?.ExtraJvmArguments is not null;
            InstanceJvmArguments = instance?.ExtraJvmArguments ?? global.ExtraJvmArguments ?? "";

            IsNewGroupOpen = false;
            RefreshAssignableGroups();
            SelectedGroup = instance?.Group is { Length: > 0 } group ? group : UngroupedFilter;

            if (JavaOptions.Count > 0)
            {
                var wanted = instance?.JavaSelection ?? InheritJava;
                InstanceJava = JavaOptions.FirstOrDefault(o => o.Value == wanted) ?? JavaOptions[0];
            }
        }
        finally
        {
            _loadingInstanceSettings = false;
        }
    }

    partial void OnOverridesMemoryChanged(bool value) => SaveInstanceSettings();
    partial void OnOverridesJvmArgumentsChanged(bool value) => SaveInstanceSettings();
    partial void OnInstanceJavaChanged(JavaOption? value) => SaveInstanceSettings();
    partial void OnInstanceJvmArgumentsChanged(string value) => SaveInstanceSettings();

    partial void OnInstanceMinMemoryMbChanged(int value)
    {
        OnPropertyChanged(nameof(InstanceMinMemoryLabel));
        if (InstanceMinMemoryMb > InstanceMaxMemoryMb) InstanceMaxMemoryMb = InstanceMinMemoryMb;
        SaveInstanceSettings();
    }

    partial void OnInstanceMaxMemoryMbChanged(int value)
    {
        OnPropertyChanged(nameof(InstanceMaxMemoryLabel));
        if (InstanceMaxMemoryMb < InstanceMinMemoryMb) InstanceMinMemoryMb = InstanceMaxMemoryMb;
        SaveInstanceSettings();
    }

    private void SaveInstanceSettings()
    {
        if (_loadingInstanceSettings || Selected is not { } instance) return;

        instance.MinMemoryMb = OverridesMemory ? InstanceMinMemoryMb : null;
        instance.MaxMemoryMb = OverridesMemory ? InstanceMaxMemoryMb : null;

        instance.JavaSelection = InstanceJava?.Value is { } java && java != InheritJava ? java : null;

        instance.ExtraJvmArguments = OverridesJvmArguments
            ? (string.IsNullOrWhiteSpace(InstanceJvmArguments) ? "" : InstanceJvmArguments.Trim())
            : null;

        _launcher.Instances.Save(instance);
        OnPropertyChanged(nameof(SettingsSummary));
    }

    public string InstanceMinMemoryLabel => $"{InstanceMinMemoryMb} MB";
    public string InstanceMaxMemoryLabel => $"{InstanceMaxMemoryMb} MB";

    /// <summary>One line under the gear telling you whether this instance runs on its own terms.</summary>
    public string SettingsSummary => Selected?.HasOverrides == true
        ? "Custom settings"
        : "Launcher defaults";

    [RelayCommand]
    private void OpenLogsFolder() => AsobuLauncher.OpenFolder(_launcher.Paths.Logs);

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

    // ---- Sharing an instance: as a file anything can open, or as a code only Asobu reads. ----

    [ObservableProperty] public partial bool IsShareOpen { get; set; }
    [ObservableProperty] public partial bool IsShareClosing { get; set; }

    /// <summary>Which pane of the sheet is up: the two doors, or the code behind one of them.</summary>
    [ObservableProperty] public partial bool IsShareChoosing { get; set; } = true;

    [ObservableProperty] public partial bool IsSharePublishing { get; set; }
    [ObservableProperty] public partial string? ShareCodeText { get; set; }
    [ObservableProperty] public partial string? ShareExpiry { get; set; }
    [ObservableProperty] public partial string? ShareError { get; set; }
    [ObservableProperty] public partial string? ShareNotice { get; set; }

    /// <summary>Said only when the same contents already had a code, which is worth explaining.</summary>
    [ObservableProperty] public partial bool ShareWasReused { get; set; }

    public bool HasShareCode => ShareCodeText is { Length: > 0 };
    public bool IsShareCodePane => !IsShareChoosing;

    partial void OnShareCodeTextChanged(string? value) => OnPropertyChanged(nameof(HasShareCode));
    partial void OnIsShareChoosingChanged(bool value) => OnPropertyChanged(nameof(IsShareCodePane));

    /// <summary>Opens the two doors. Which instance is shared is whichever is selected.</summary>
    public void OpenShare()
    {
        if (Selected is null) return;

        ShareCodeText = null;
        ShareExpiry = null;
        ShareError = null;
        ShareNotice = null;
        ShareWasReused = false;
        IsShareChoosing = true;
        IsShareOpen = true;
    }

    [RelayCommand]
    private async Task CloseShareAsync()
    {
        if (!IsShareOpen || IsShareClosing) return;

        IsShareClosing = true;
        await Task.Delay(240);
        IsShareClosing = false;
        IsShareOpen = false;
    }

    /// <summary>
    /// Reads the instance, asks Asobu for a code, and shows it.
    ///
    /// The same contents always come back as the same code, so pressing this twice is not a way
    /// to make a second one — it winds the existing code's week forward instead, which is what
    /// makes a code you have already sent someone keep working.
    /// </summary>
    [RelayCommand]
    private async Task ShareAsCodeAsync()
    {
        if (Selected is not { } instance) return;

        IsShareChoosing = false;
        IsSharePublishing = true;
        ShareError = null;

        try
        {
            var described = await Task.Run(() => _launcher.Shares.DescribeAsync(instance));

            if (described.Files.Count == 0)
                ShareNotice = "This instance has no mods or packs, so the code carries only its version and loader.";

            var code = await _launcher.Shares.PublishAsync(described);

            ShareCodeText = code.Code;
            ShareExpiry = code.ExpiryLabel;
            ShareWasReused = code.Reused;
        }
        catch (FriendsAuthException)
        {
            ShareError = "Sharing by code needs a Microsoft account. Sign in from Accounts, then try again.";
        }
        catch (FriendsException e)
        {
            ShareError = e.Message;
        }
        catch (Exception e)
        {
            ShareError = e.Message;
        }
        finally
        {
            IsSharePublishing = false;
        }
    }

    /// <summary>Back to the two doors, so the other one can be taken.</summary>
    [RelayCommand]
    private void BackToShareChoice()
    {
        ShareError = null;
        ShareNotice = null;
        IsShareChoosing = true;
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
    private Task PlayAsync() => LaunchAsync(Selected);

    /// <summary>
    /// Launch straight from a library card without opening its page. Takes the instance as a
    /// parameter rather than leaning on Selected, so hovering one card and hitting play can
    /// never start whichever instance happened to be selected last.
    /// </summary>
    [RelayCommand]
    private Task QuickPlayAsync(Instance? instance) => LaunchAsync(instance);

    private async Task LaunchAsync(Instance? target)
    {
        if (target is not { } instance) return;
        if (IsBusy || IsRunning) return;

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

            // A fresh session starts a fresh log; the previous run's lines are on disk.
            ClearLog();
            _logInstance = instance;
            OnPropertyChanged(nameof(LogTitle));

            var process = await _launcher.LaunchAsync(instance, session, reporter, AppendLog);

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

    // ---- Live game output. The launcher already captures every line to a file; this is the
    // same lines, kept in memory so they can be read while the game is still running.

    /// <summary>
    /// How many lines are kept. Minecraft is chatty — a modded start-up runs to thousands — and
    /// the whole session is on disk regardless, so this only bounds what is held for reading.
    /// </summary>
    private const int LogLineLimit = 2000;

    private readonly object _logGate = new();
    private readonly Queue<GameLogLine> _logLines = new();
    private DispatcherTimer? _logTimer;

    /// <summary>
    /// Unpacks the log4j XML the game writes into readable lines. Per session and stateful: one
    /// event arrives over several callbacks, so it has to remember where it is.
    /// </summary>
    private GameLogFormatter _logFormat = new();

    /// <summary>
    /// Whose output is in the buffer. Not the selected instance: the library's strip can be on
    /// screen while a different card is clicked, and the log belongs to the game that is running
    /// rather than to whatever is highlighted.
    /// </summary>
    private Instance? _logInstance;

    /// <summary>Named on the sheet, so there is no doubt which instance is talking.</summary>
    public string LogTitle => _logInstance is { } instance ? $"{instance.Name} — game output" : "Game output";

    [ObservableProperty] public partial bool IsLogOpen { get; set; }
    [ObservableProperty] public partial bool IsLogClosing { get; set; }

    /// <summary>The lines as they stand, refreshed on a timer rather than per line.</summary>
    [ObservableProperty] public partial IReadOnlyList<GameLogLine> LogLines { get; set; } = [];

    public bool HasLog => LogLines.Count > 0;

    partial void OnLogLinesChanged(IReadOnlyList<GameLogLine> value) => OnPropertyChanged(nameof(HasLog));

    /// <summary>
    /// Called from whichever thread the process writes on. Nothing is posted to the UI from
    /// here: a chatty modpack would flood the dispatcher with thousands of tiny messages, so the
    /// lines are buffered and the screen pulls from them a few times a second instead.
    /// </summary>
    private void AppendLog(string line)
    {
        lock (_logGate)
        {
            // One raw line can produce none — an event that has not closed — or a dozen, when a
            // stack trace arrives all at once.
            foreach (var formatted in _logFormat.Feed(line))
            {
                _logLines.Enqueue(formatted);
                while (_logLines.Count > LogLineLimit) _logLines.Dequeue();
            }
        }
    }

    private void ClearLog()
    {
        lock (_logGate)
        {
            _logLines.Clear();
            _logFormat = new GameLogFormatter();
        }

        LogLines = [];
    }

    [RelayCommand]
    private void ViewLiveLog()
    {
        // Whichever instance the buffer belongs to, falling back to the one on screen for a
        // session that ended before the sheet was opened.
        _logInstance ??= Selected;
        if (_logInstance is null) return;

        OnPropertyChanged(nameof(LogTitle));
        RefreshLogText();

        IsLogClosing = false;
        IsLogOpen = true;

        // Only while it is being looked at. A timer running behind a closed sheet would rebuild
        // a string nobody is reading, for as long as the game is up.
        _logTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => RefreshLogText());

        _logTimer.Start();
    }

    [RelayCommand]
    private async Task CloseLiveLogAsync()
    {
        _logTimer?.Stop();

        if (!IsLogOpen || IsLogClosing) return;

        IsLogClosing = true;
        await Task.Delay(ModalSlideMilliseconds);
        IsLogClosing = false;
        IsLogOpen = false;
    }

    private void RefreshLogText()
    {
        GameLogLine[] lines;
        lock (_logGate) lines = [.. _logLines];

        // Only when something was actually added, so a quiet game does not rebuild every run four
        // times a second — and, more to the point, does not keep yanking the view back down.
        if (lines.Length == LogLines.Count) return;

        LogLines = lines;
    }

    /// <summary>Opens the file the same lines are being written to, for anything past the cap.</summary>
    [RelayCommand]
    private void OpenLogFile()
    {
        if ((_logInstance ?? Selected) is not { } instance) return;

        if (_launcher.LatestLogFor(instance) is { Length: > 0 } path) AsobuLauncher.OpenUrl(path);
        else AsobuLauncher.OpenFolder(_launcher.Paths.Logs);
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

            // Whatever it said on the way out, and then nothing more: a finished log does not
            // need pulling from four times a second for as long as the sheet stays open.
            RefreshLogText();
            _logTimer?.Stop();

            IsRunning = false;
            Status = "";

            OnPropertyChanged(nameof(Selected));
            ApplyFilter();

            // A non-zero exit after the game was up means a crash, not a normal quit — unless
            // someone ended it themselves, which exits the same way and is not news.
            var crashed = exitCode != 0 && !_killed;

            _killed = false;

            _ = ReviewSessionAsync(instance, exitCode, crashed);
        });
    }

    /// <summary>
    /// Everything that happens after the game closes: say what went wrong, then offer to fix it.
    ///
    /// One path rather than two so the crash is analysed once. The analysis walks every jar in
    /// the instance for its metadata, and the verdict on screen and the rows in the sheet want
    /// the same answer — reading it twice would double the most expensive thing either does.
    /// </summary>
    private async Task ReviewSessionAsync(Instance instance, int exitCode, bool crashed)
    {
        var analysis = crashed ? await ExplainCrashAsync(instance, exitCode) : null;

        await CheckForProblemsAsync(instance, analysis);
    }

    /// <summary>
    /// Reads the session that just ended and says what went wrong, rather than telling someone to
    /// go and look. The same analysis the crash reports page runs — a crash is the moment it is
    /// worth having, and making people find it afterwards wastes the one thing it is good at.
    ///
    /// Returns what it found so the sheet can offer to act on it without doing the work again.
    /// </summary>
    private async Task<CrashAnalysis?> ExplainCrashAsync(Instance instance, int exitCode)
    {
        var fallback = $"Minecraft exited with code {exitCode}. Check the crash reports.";

        if (_launcher.LatestLogFor(instance) is not { Length: > 0 } path)
        {
            Error = fallback;
            return null;
        }

        try
        {
            var directory = ModScanner.ModsDirectory(_launcher.Paths, instance.Folder);

            // Off the UI thread: reading a large log and scanning every jar for its metadata is
            // not something to do while the window is meant to be repainting.
            var analysis = await Task.Run(async () =>
            {
                var mods = ModScanner.Scan(directory);
                var text = await CrashReports.ReadAsync(path);

                return CrashAnalyzer.Analyze(text, mods);
            });

            // Someone has moved on to another instance. The verdict belongs to this one and
            // would be nonsense over there, but the analysis is still worth handing back: the
            // sheet checks the same thing before it opens.
            if (Selected?.Id != instance.Id) return analysis;

            // "No crash in this log" is a finding about the log, not about the session. Shown as
            // a verdict it reads as an error message insisting nothing is wrong, which is worse
            // than the plain exit code it replaced.
            Error = analysis.Cause == CrashCause.Clean ? fallback
                : analysis.HasVerdict ? $"{analysis.Headline}. {analysis.Advice}"
                : fallback;

            return analysis;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = fallback;
            return null;
        }
    }

    // ---- Changing the instance's mod loader, and taking its mods with it ----

    /// <summary>One installed mod and what becomes of it, as a row in the prompt.</summary>
    public partial class MoveRow(ModMove move) : ViewModelBase
    {
        public ModMove Move { get; } = move;

        public string Name => Move.Name;
        public string Summary => Move.Summary;
        public bool CanMove => Move.CanMove;

        [ObservableProperty] public partial bool IsMoving { get; set; }
        [ObservableProperty] public partial bool IsMoved { get; set; }
        [ObservableProperty] public partial string? Notice { get; set; }

        public bool IsPending => CanMove && !IsMoved && !IsMoving;

        partial void OnIsMovedChanged(bool value) => OnPropertyChanged(nameof(IsPending));

        partial void OnIsMovingChanged(bool value) => OnPropertyChanged(nameof(IsPending));
    }

    /// <summary>Loaders offered for this instance, filled in when the settings sheet opens.</summary>
    public ObservableCollection<string> LoaderChoices { get; } = [];

    [ObservableProperty] public partial string? SelectedLoader { get; set; }
    [ObservableProperty] public partial bool IsCheckingLoaderChange { get; set; }

    public ObservableCollection<MoveRow> Moves { get; } = [];

    [ObservableProperty] public partial bool IsMovePromptOpen { get; set; }
    [ObservableProperty] public partial bool IsMovePromptClosing { get; set; }

    public string MoveQuestion => $"Move these mods to {SelectedLoader}?";

    public bool HasStuckMods => Moves.Any(row => !row.CanMove);

    /// <summary>
    /// Nothing at all can move. Almost always a loader that this Minecraft version's ecosystem
    /// has left behind — Forge still publishes for the newest versions, but the mods went to
    /// NeoForge — and that is worth saying plainly rather than repeating "no build" six times
    /// and leaving someone on a loader none of their mods will load under.
    /// </summary>
    public bool NothingCanMove => Moves.Count > 0 && Moves.All(row => !row.CanMove);

    public string StuckSummary => _loaderBefore is { Length: > 0 } before
        ? $"None of these have a {SelectedLoader} build for Minecraft {Selected?.MinecraftVersion}. "
          + $"This instance is on {SelectedLoader} now, and nothing in it will load."
        : "";

    public string RevertLabel => $"Put it back on {_loaderBefore}";

    /// <summary>What the instance was on before the dropdown was touched, so it can go back.</summary>
    private string? _loaderBefore;

    /// <summary>
    /// A loader change made in the settings sheet whose mods have not been dealt with yet. Held
    /// until the sheet is closed: the question is about the whole instance, and asking it over a
    /// sheet that is still open would be asking twice if the picker is touched again.
    /// </summary>
    private string? _pendingMoveLoader;

    /// <summary>
    /// The plan for that loader, started the moment the picker moves rather than after Done.
    ///
    /// It costs nothing to be wrong: the plan only reads the mods folder and asks the shops what
    /// they publish, so running it for a loader nobody ends up choosing changes nothing. What it
    /// buys is the pause after Done — by then the answer is usually already in hand.
    /// </summary>
    private Task<IReadOnlyList<ModMove>>? _movePlan;

    private string? _movePlanFor;
    private CancellationTokenSource? _movePlanCts;

    /// <summary>Set while the loader is being changed, so the picker cannot be raced.</summary>
    private bool _loadingLoaderChoices;

    /// <summary>
    /// Which loaders exist for this instance's Minecraft version. Asked rather than assumed:
    /// Fabric does not exist before 1.14 and NeoForge not before 1.20.2, and offering a loader
    /// that cannot be installed is offering a dead end.
    /// </summary>
    private async Task LoadLoaderChoicesAsync(Instance instance)
    {
        _loadingLoaderChoices = true;

        try
        {
            LoaderChoices.Clear();
            LoaderChoices.Add("Vanilla");

            var version = instance.MinecraftVersion;

            var fabric = _launcher.Fabric.GetLatestLoaderAsync(version);
            var quilt = _launcher.Quilt.GetLatestLoaderAsync(version);
            var forge = _launcher.Loaders.GetForgeVersionAsync(version);
            var neoForge = _launcher.Loaders.GetNeoForgeVersionAsync(version);

            await Task.WhenAll(fabric, quilt, forge, neoForge);

            if (Selected?.Id != instance.Id)
            {
                // Nothing to pick from and nothing to pick: leave the box empty rather than
                // showing a loader for an instance that is no longer on screen.
                SelectedLoader = null;
                return;
            }

            if (fabric.Result is { Length: > 0 }) LoaderChoices.Add("Fabric");
            if (forge.Result is { Length: > 0 }) LoaderChoices.Add("Forge");
            if (neoForge.Result is { Length: > 0 }) LoaderChoices.Add("NeoForge");
            if (quilt.Result is { Length: > 0 }) LoaderChoices.Add("Quilt");

            // The one it is on has to be in the list even where the service is unreachable, or
            // opening settings would silently look like the loader had changed.
            if (!LoaderChoices.Contains(instance.LoaderName)) LoaderChoices.Add(instance.LoaderName);

            // Set once, and only now. Assigning it before the list held the item left the box
            // with nothing to select, and the second assignment then matched the value already
            // stored — no change, no notification, and a picker that stayed blank over an
            // instance that plainly has a loader.
            SelectedLoader = null;
            SelectedLoader = instance.LoaderName;
        }
        finally
        {
            _loadingLoaderChoices = false;
        }
    }

    partial void OnSelectedLoaderChanged(string? value)
    {
        if (_loadingLoaderChoices) return;

        // Only noted. Everything else in this sheet saves as you change it, but a loader is not
        // a setting — it decides which mods can load — and applying one the instant the list is
        // opened would act on a choice nobody has finished making. Done is what commits it.
        _pendingMoveLoader = value;

        StartMovePlan(value);
    }

    /// <summary>
    /// The plan, and then the files it names — fetched into the cache while the sheet is still
    /// open. Nothing touches the instance: a Cancel has to leave the mods folder as it was, and
    /// what is left behind is a few jars in the cache, which is what a cache is for.
    /// </summary>
    private async Task<IReadOnlyList<ModMove>> PlanAndPrefetchAsync(
        Instance instance, string loader, CancellationToken cancellationToken)
    {
        var plan = await _launcher.PlanLoaderMoveAsync(instance, loader, cancellationToken);

        try
        {
            await _launcher.PrefetchMovesAsync(plan, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A prefetch that failed costs nothing: applying the move downloads it properly and
            // reports its own failure. This is only ever an optimisation.
        }

        return plan;
    }

    /// <summary>
    /// Works out what would become of the mods, ahead of being asked. Started on every change of
    /// the picker and thrown away on the next one, so whichever loader is settled on has had its
    /// answer worked out while the sheet was still open.
    /// </summary>
    private void StartMovePlan(string? loaderName)
    {
        _movePlanCts?.Cancel();
        _movePlanCts?.Dispose();
        _movePlanCts = null;
        _movePlan = null;
        _movePlanFor = null;

        if (Selected is not { } instance || loaderName is not { Length: > 0 }) return;
        if (loaderName.Equals(instance.LoaderName, StringComparison.OrdinalIgnoreCase)) return;

        var loader = loaderName.ToLowerInvariant();
        var request = new CancellationTokenSource();

        _movePlanCts = request;
        _movePlanFor = loader;

        _movePlan = PlanAndPrefetchAsync(instance, loader, request.Token);

        // Nothing awaits this until Done, and an unobserved failure on a speculative request
        // should not come back as an unhandled exception later.
        _ = _movePlan.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Applies the loader the picker was left on. Returns the loader that was chosen, or null
    /// when nothing changed or it could not be done.
    /// </summary>
    private async Task<string?> ApplyLoaderChoiceAsync(Instance instance, string loaderName)
    {
        var loader = loaderName.ToLowerInvariant();

        if (loaderName.Equals(instance.LoaderName, StringComparison.OrdinalIgnoreCase)) return null;

        _loaderBefore = instance.LoaderName;

        try
        {
            if (await _launcher.SetLoaderAsync(instance, loader) is { } problem)
            {
                Error = problem;
                return null;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }

        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SettingsSummary));
        ApplyFilter();

        return loader;
    }

    [RelayCommand]
    private async Task MoveModAsync(MoveRow? row)
    {
        if (row is null || Selected is not { } instance || row.IsMoving || !row.CanMove) return;

        row.IsMoving = true;
        row.Notice = null;

        try
        {
            var result = await _launcher.ApplyMoveAsync(instance, row.Move);

            row.IsMoved = result.Swapped;
            row.Notice = result.Swapped ? result.Reason ?? $"Now {result.Installed}" : result.Reason;
        }
        catch (Exception ex)
        {
            row.Notice = ex.Message;
        }
        finally
        {
            row.IsMoving = false;
        }

        if (!Moves.Where(move => move.CanMove).All(move => move.IsMoved)) return;

        await LoadModsAsync(instance);
        await Task.Delay(SwapSettleMilliseconds);

        // Anything that could not move keeps the sheet up: that list is the whole point of it.
        if (!HasStuckMods) await DismissMovesAsync();
    }

    [RelayCommand]
    private async Task MoveAllModsAsync()
    {
        foreach (var row in Moves.Where(move => move.IsPending).ToList())
            await MoveModAsync(row);
    }

    /// <summary>
    /// Undoes the loader change. Offered when nothing could move: the instance is sitting on a
    /// loader none of its mods will load under, and the shortest way out is the way back.
    /// </summary>
    [RelayCommand]
    private async Task RevertLoaderAsync()
    {
        if (Selected is not { } instance || _loaderBefore is not { Length: > 0 } before) return;

        IsCheckingLoaderChange = true;

        try
        {
            await _launcher.SetLoaderAsync(instance, before.ToLowerInvariant());

            _loadingLoaderChoices = true;
            SelectedLoader = instance.LoaderName;
            _loadingLoaderChoices = false;

            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(SettingsSummary));
            ApplyFilter();
        }
        finally
        {
            IsCheckingLoaderChange = false;
        }

        await DismissMovesAsync();
    }

    [RelayCommand]
    private async Task DismissMovesAsync()
    {
        if (IsMovePromptClosing) return;

        IsMovePromptClosing = true;
        await Task.Delay(ModalSlideMilliseconds);

        IsMovePromptOpen = false;
        IsMovePromptClosing = false;

        if (Selected is { } instance) await LoadModsAsync(instance);
    }

    // ---- Everything the last session complained about ----

    /// <summary>What kind of trouble a row describes, and therefore what putting it right means.</summary>
    public enum ProblemKind
    {
        /// <summary>A mod is at a build another mod refuses to sit with. Swap it.</summary>
        Conflict,

        /// <summary>A mod the loader asked for and could not find. Fetch it.</summary>
        Missing,

        /// <summary>The mod a crash points at. Turn it off.</summary>
        BadMod,

        /// <summary>The game ran out of memory and there is room to give it more.</summary>
        Memory,
    }

    /// <summary>
    /// One thing that went wrong, whichever of the three kinds it is, and what has been done
    /// about it.
    ///
    /// One row type rather than three because they are one question on screen: something is
    /// wrong, here is the button that fixes it. The kinds differ in what the button does and what
    /// it is called, which is a switch in two places rather than three of everything.
    /// </summary>
    public partial class ProblemRow : ViewModelBase
    {
        private ProblemRow(ProblemKind kind, string headline, string detail)
        {
            Kind = kind;
            Headline = headline;
            Detail = detail;
        }

        public static ProblemRow For(ModConflict conflict) =>
            new(ProblemKind.Conflict, conflict.Headline, conflict.Detail) { Conflict = conflict };

        public static ProblemRow For(MissingDependency missing) =>
            new(ProblemKind.Missing, missing.Headline, "Not installed") { Missing = missing };

        public static ProblemRow For(CrashSuspect suspect) =>
            new(ProblemKind.BadMod, $"{suspect.Name} looks like the cause", suspect.ConfidenceLabel)
            {
                Suspect = suspect,
            };

        public static ProblemRow ForMemory(int fromMb, int toMb) =>
            new(ProblemKind.Memory, "Minecraft ran out of memory",
                $"Running on {Gigabytes(fromMb)}. Asobu can give it {Gigabytes(toMb)}.")
            {
                RaiseMemoryToMb = toMb,
            };

        private static string Gigabytes(int megabytes) =>
            (megabytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB";

        public ProblemKind Kind { get; }
        public string Headline { get; }
        public string Detail { get; }

        public ModConflict? Conflict { get; private init; }
        public MissingDependency? Missing { get; private init; }
        public CrashSuspect? Suspect { get; private init; }
        public int? RaiseMemoryToMb { get; private init; }

        /// <summary>
        /// Every name this row's mod goes by, for keeping two findings about one mod off the
        /// screen. Both the id and the display name, because the three finders do not agree on
        /// which they report — a crash names "Sodium" where the loader said "sodium".
        /// </summary>
        public IReadOnlyList<string> Names => Kind switch
        {
            ProblemKind.Conflict => [Conflict!.ModId, Conflict.ModName],
            ProblemKind.Missing => [Missing!.Id, Missing.Name],
            ProblemKind.BadMod => [Suspect!.Name, Suspect.FileName],

            // Not about a mod, so there is nothing for another row to collide with.
            _ => [],
        };

        public string ActionLabel => Kind switch
        {
            ProblemKind.Conflict => "Swap",
            ProblemKind.Missing => "Get it",
            ProblemKind.BadMod => "Turn off",
            _ => "Give it more",
        };

        public string BusyLabel => Kind switch
        {
            ProblemKind.Conflict => "Swapping…",
            ProblemKind.Missing => "Fetching…",
            ProblemKind.BadMod => "Turning off…",
            _ => "Saving…",
        };

        public string DoneLabel => Kind switch
        {
            ProblemKind.Conflict => "Swapped",
            ProblemKind.Missing => "Added",
            ProblemKind.BadMod => "Off",
            _ => "Raised",
        };

        [ObservableProperty] public partial bool IsFixing { get; set; }
        [ObservableProperty] public partial bool IsDone { get; set; }
        [ObservableProperty] public partial string? Notice { get; set; }

        public bool CanFix => !IsFixing && !IsDone;
        public bool HasNotice => Notice is { Length: > 0 };

        partial void OnIsFixingChanged(bool value) => OnPropertyChanged(nameof(CanFix));
        partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(CanFix));
        partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));
    }

    public ObservableCollection<ProblemRow> Problems { get; } = [];

    [ObservableProperty] public partial bool IsProblemsPromptOpen { get; set; }
    [ObservableProperty] public partial bool IsProblemsPromptClosing { get; set; }

    private Instance? _problemsInstance;

    public string ProblemsQuestion => Problems.Count == 1
        ? Problems[0].Kind switch
        {
            ProblemKind.Conflict => "One mod wants a different version of another",
            ProblemKind.Missing => "A mod is missing something it needs",
            ProblemKind.BadMod => "One mod looks like the cause",
            _ => "That session ran out of memory",
        }
        : $"{Problems.Count} things went wrong in that session";

    /// <summary>A beat to see the last row land before the sheet takes itself away.</summary>
    private const int SwapSettleMilliseconds = 850;

    /// <summary>
    /// Reads the log the session just wrote and offers to put right everything it complained
    /// about, in one sheet.
    ///
    /// One sheet because they arrive together and they are one thought: a mod set that will not
    /// start usually has a missing dependency and a version disagreement at the same time, and
    /// answering them one modal at a time — which is what this did before, with the second
    /// suppressed while the first was open — meant relaunching into the same wall to be told
    /// about the next one.
    ///
    /// Asked after the game closes rather than acted on quietly: changing someone's mods without
    /// saying so is not on, and mid-session is the worst possible moment for it.
    /// </summary>
    private async Task CheckForProblemsAsync(Instance instance, CrashAnalysis? analysis)
    {
        if (_launcher.LatestLogFor(instance) is not { Length: > 0 } path) return;

        // One read of the log, off the UI thread. Each finder used to open the file for itself,
        // which on a long session is the same megabytes read twice while the window is meant to
        // be repainting.
        var found = await Task.Run(async () =>
        {
            var rows = new List<ProblemRow>();

            try
            {
                var log = await File.ReadAllTextAsync(path).ConfigureAwait(false);

                rows.AddRange(ModConflicts.Find(log).Select(ProblemRow.For));
                rows.AddRange(MissingDependencies.Find(log).Select(ProblemRow.For));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return rows;
            }

            // Only when the analyser blames a mod outright. An out-of-memory kill or a graphics
            // fault has no mod to turn off, and offering to turn one off anyway would be a guess
            // wearing a fix's clothes.
            if (analysis is { Cause: CrashCause.Mod })
            {
                // Every mod the crash named, or — when it named none — the single likeliest of
                // the ones that merely turned up in the stack trace. One, because past the first
                // the ranking is guesswork and a list of six mods to turn off is not a fix, it is
                // the log again with buttons. Each row says which of the two it is.
                var named = analysis.Suspects.Where(suspect => suspect.NamedDirectly).ToList();

                rows.AddRange((named.Count > 0 ? named : analysis.Suspects.Take(1)).Select(ProblemRow.For));
            }

            // Ran out of memory, and there is room to give it more. Offered only when raising it
            // would actually change something: at the machine's own limit this is a different
            // problem, and a button that sets the number it is already on wastes the click.
            if (analysis is { Cause: CrashCause.OutOfMemory }
                && MemoryPlanner.RaisedFor(_launcher.Paths, instance) is { } raised)
            {
                rows.Add(ProblemRow.ForMemory(
                    MemoryPlanner.CurrentMaxMemoryMb(_launcher.Paths, instance), raised));
            }

            return rows;
        });

        // Two findings about one mod is one problem described twice, and offering both invites
        // fixing it twice. Order decides which survives: a conflict names the build to move to,
        // which beats a dependency row, which beats "this one looks suspicious".
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Problems.Clear();

        foreach (var row in found)
        {
            if (row.Names.Any(seen.Contains)) continue;

            foreach (var name in row.Names) seen.Add(name);
            Problems.Add(row);
        }

        if (Problems.Count == 0) return;

        _problemsInstance = instance;
        OnPropertyChanged(nameof(ProblemsQuestion));

        IsProblemsPromptClosing = false;
        IsProblemsPromptOpen = true;
    }

    [RelayCommand]
    private async Task FixAsync(ProblemRow? row)
    {
        if (row is null || _problemsInstance is not { } instance || row.IsFixing) return;

        row.IsFixing = true;
        row.Notice = null;

        try
        {
            var (done, notice) = await ApplyAsync(instance, row);

            row.IsDone = done;
            row.Notice = notice;
        }
        catch (Exception e)
        {
            row.Notice = e.Message;
        }
        finally
        {
            row.IsFixing = false;
        }

        // Nothing left to decide once every row is dealt with, so the sheet goes rather than
        // waiting to be dismissed. A row that failed keeps it open, which is the point: that one
        // still needs reading.
        if (!Problems.All(problem => problem.IsDone)) return;

        await LoadModsAsync(instance);

        // Long enough to read the last answer. Closing the instant it lands would flash it past.
        await Task.Delay(SwapSettleMilliseconds);

        await DismissProblemsAsync();
    }

    /// <summary>Doing the thing the row's button says, which is the only place the kinds differ.</summary>
    private async Task<(bool Done, string? Notice)> ApplyAsync(Instance instance, ProblemRow row)
    {
        switch (row.Kind)
        {
            case ProblemKind.Conflict:
            {
                var result = await _launcher.SwapModAsync(instance, row.Conflict!);

                return (result.Swapped, result.Swapped
                    ? result.Reason ?? $"Now {result.Installed}"
                    : result.Reason);
            }

            case ProblemKind.Missing:
            {
                var found = await _launcher.FindDependencyAsync(instance, row.Missing!);

                if (found is null) return (false, $"Neither shop has a mod called {row.Missing!.Name}.");

                var result = await _launcher.InstallModAsync(instance, found);

                return (result.Installed, result.Installed
                    ? $"Added {result.FileName}"
                    : result.Reason
                      ?? (result.Blocked
                          ? "The author allows downloads from their page only."
                          : $"No build for {instance.LoaderName} {instance.MinecraftVersion}."));
            }

            case ProblemKind.Memory:
            {
                // The floor moves with the ceiling: -Xms well under -Xmx is what lets the JVM
                // grow into what the pack needs instead of reserving it all up front.
                instance.MaxMemoryMb = row.RaiseMemoryToMb;
                instance.MinMemoryMb = MemoryPlanner.MinMemoryMbFor(row.RaiseMemoryToMb!.Value);

                _launcher.Instances.Save(instance);

                return (true, "Saved. It takes effect next time this instance starts.");
            }

            default:
            {
                // Turned off, never deleted. The accusation is a heuristic and says as much on
                // screen; a mod that was only renamed can be turned back on by someone who
                // disagrees, where a deleted one has to be found and downloaded again.
                var directory = ModScanner.ModsDirectory(_launcher.Paths, instance.Folder);

                var mod = await Task.Run(() => ModScanner.Scan(directory).FirstOrDefault(candidate =>
                    string.Equals(candidate.FileName, row.Suspect!.FileName, StringComparison.OrdinalIgnoreCase)));

                if (mod is null) return (false, $"{row.Suspect!.Name} is no longer in this instance.");
                if (!mod.Enabled) return (true, "Already turned off.");

                ModScanner.SetEnabled(mod, false);

                return (true, $"Turned off {mod.FileName}. You can turn it back on from Mods.");
            }
        }
    }

    /// <summary>
    /// The one-click answer, and the reason the sheet is worth having at all: everything it
    /// found, put right, without reading a line of it.
    /// </summary>
    [RelayCommand]
    private async Task FixEverythingAsync()
    {
        foreach (var row in Problems.Where(row => row.CanFix).ToList())
            await FixAsync(row);
    }

    [RelayCommand]
    private async Task DismissProblemsAsync()
    {
        if (IsProblemsPromptClosing) return;

        IsProblemsPromptClosing = true;
        await Task.Delay(ModalSlideMilliseconds);

        IsProblemsPromptOpen = false;
        IsProblemsPromptClosing = false;
        _problemsInstance = null;
    }


    // ---- Mods with a newer build ----

    [ObservableProperty] public partial bool IsCheckingUpdates { get; set; }

    /// <summary>
    /// Whether the update column is worth its width. A finished update deliberately does not
    /// count: its row goes back to being an ordinary one the instant the new file is in, which
    /// is what makes the column disappear behind the last mod that needed it.
    /// </summary>
    public bool HasUpdates => Mods.Any(mod => mod.HasUpdate || mod.IsUpdating);

    private void UpdatesChanged() => OnPropertyChanged(nameof(HasUpdates));

    private async Task CheckForUpdatesAsync(Instance instance, IReadOnlyList<ModEntry> found)
    {
        IsCheckingUpdates = true;

        try
        {
            var updates = await _launcher.FindUpdatesAsync(instance, found);

            // The sheet may have been closed, another instance opened, or the dropdown moved to
            // a kind that has no updates to speak of, while this was out.
            if (Selected?.Id != instance.Id || !ChecksUpdates) return;

            var byPath = updates.ToDictionary(update => update.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var row in Mods)
                if (byPath.TryGetValue(row.Path, out var update))
                    row.Update = update;

            SortMods();
            UpdatesChanged();
        }
        catch (Exception)
        {
            // No update column is a mods list without one, not a broken page.
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    /// <summary>
    /// Anything behind goes to the top, the rest stays in the order the folder was read in. The
    /// point of the column is to be noticed, and a mod needing an update three screens down is
    /// the same as no column at all.
    /// </summary>
    private void SortMods()
    {
        var ordered = Mods
            .OrderByDescending(mod => mod.HasUpdate)
            .ThenBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var at = Mods.IndexOf(ordered[i]);
            if (at != i) Mods.Move(at, i);
        }
    }

    [RelayCommand]
    private async Task UpdateModAsync(ModRowViewModel? row)
    {
        if (row is null || Selected is not { } instance) return;
        if (row.Update is not { } update || row.IsUpdating) return;

        row.IsUpdating = true;
        row.Error = null;
        UpdatesChanged();

        try
        {
            var result = await _launcher.ApplyUpdateAsync(instance, update);

            if (!result.Swapped)
            {
                // The offer stands, so the button comes back with the reason beside it.
                row.Error = result.Reason;
                return;
            }

            await RefreshAfterUpdateAsync(instance, row, result.Installed!);
        }
        catch (Exception ex)
        {
            row.Error = ex.Message;
        }
        finally
        {
            // The column's visibility is asked for last, and only here: the row counts as
            // updating until this line, so anything checking before it would keep a column open
            // for work that has already finished.
            row.IsUpdating = false;
            UpdatesChanged();
        }
    }

    /// <summary>
    /// Puts the row back in step with the file that replaced its jar. Read off the disk rather
    /// than patched from the update record: size and name are facts about what actually landed.
    /// </summary>
    private async Task RefreshAfterUpdateAsync(Instance instance, ModRowViewModel row, string installed)
    {
        var directory = ModScanner.ModsDirectory(_launcher.Paths, instance.Folder);
        var entry = await Task.Run(() => ModScanner.ReadOne(Path.Combine(directory, installed)));

        // Even where the new file cannot be read back, the update did happen: the row must stop
        // offering it either way, or it would sit there advertising a build it already has.
        if (entry is not null) row.Adopt(entry);
        else
        {
            row.Update = null;
            row.IsUpdated = false;
        }

        SortMods();
    }

    /// <summary>Fetches every newer build in one go, for a folder that has fallen well behind.</summary>
    [RelayCommand]
    private async Task UpdateAllModsAsync()
    {
        // Each row refreshes itself as its update lands, so there is nothing to reload after:
        // re-reading the folder here would blank a list the user is watching, to arrive at what
        // is already on screen.
        foreach (var row in Mods.Where(mod => mod.HasUpdate).ToList())
            await UpdateModAsync(row);
    }

    /// <summary>
    /// True from the moment Kill is pressed until that session is done with. An instance someone
    /// ended on purpose exits non-zero like any crash, and diagnosing it would answer a question
    /// nobody asked.
    /// </summary>
    private bool _killed;

    [RelayCommand]
    private void Kill()
    {
        if (_process is not { } process) return;

        _killed = true;

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
            AsobuLauncher.OpenFolder(_launcher.Paths.InstanceGameDir(instance.Folder));
    }

    [RelayCommand]
    private void AskDelete(Instance? instance)
    {
        // Instances hold worlds. One click must never be enough.
        if (instance is null) return;

        PendingDelete = instance;
        IsDeleteConfirmOpen = true;
    }

    [RelayCommand]
    private async Task CancelDeleteAsync() => await CloseDeleteConfirmAsync();

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } instance) return;

        _launcher.Instances.Delete(instance);
        await CloseDeleteConfirmAsync();

        PendingDelete = null;
        DismissSheets();

        // Only leave the instance page if the thing that was deleted is what it was showing.
        if (Selected?.Id == instance.Id)
        {
            Selected = null;
            IsDetailOpen = false;
            IsLibraryVisible = true;
        }

        Reload();
    }

    private async Task CloseDeleteConfirmAsync()
    {
        if (!IsDeleteConfirmOpen || IsDeleteConfirmClosing) return;

        IsDeleteConfirmClosing = true;
        await Task.Delay(ModalSlideMilliseconds);
        IsDeleteConfirmClosing = false;
        IsDeleteConfirmOpen = false;
    }

    // ---- Card context menu. Each takes its instance rather than leaning on Selected, so
    // right-clicking a card can never act on whichever one happened to be open last. ----

    /// <summary>
    /// Pins or unpins, which is to say puts the instance in the Pinned group or takes it out.
    /// Unpinning returns it to Ungrouped rather than to whatever group it was in before: a
    /// remembered previous group would be a second piece of state to keep honest, and the group
    /// is a label anyone can set back in one click.
    /// </summary>
    [RelayCommand]
    private void TogglePin(Instance? instance)
    {
        if (instance is null) return;

        instance.Group = instance.IsPinned ? null : Instance.PinnedGroup;
        _launcher.Instances.Save(instance);

        RefreshGroups();
        ApplyFilter();
    }

    [RelayCommand]
    private void CloneFor(Instance? instance)
    {
        if (instance is null) return;

        var clone = _launcher.Instances.Clone(instance);
        Reload();
        Selected = _all.FirstOrDefault(i => i.Id == clone.Id);
    }

    [RelayCommand]
    private void OpenEditFor(Instance? instance)
    {
        if (instance is null) return;

        // Selected rather than opened, exactly as for settings below: the sheet is a modal over
        // the library, which is where the menu was used.
        Selected = instance;
        OpenEdit();
    }

    [RelayCommand]
    private void OpenSettingsFor(Instance? instance)
    {
        if (instance is null) return;

        // Selected, not opened. The settings sheet is a modal over whatever is on screen, and
        // what is on screen is the library the menu was used in — walking into the instance page
        // first meant closing the sheet left you somewhere you never asked to go.
        Selected = instance;
        OpenInstanceSettings();
    }
}
