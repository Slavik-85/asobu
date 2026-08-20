using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Minecraft;
using Asobu.Core.Mods;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Asobu.App.ViewModels;

/// <summary>One row in the version list.</summary>
public sealed class VersionRow(VersionSummary summary)
{
    public VersionSummary Summary { get; } = summary;

    public string Id => Summary.Id;

    public string Kind => Summary.Type switch
    {
        "release" => "Release",
        "snapshot" => "Snapshot",
        "old_beta" => "Beta",
        "old_alpha" => "Alpha",
        var other => other,
    };

    public string Released => Summary.ReleaseTime.ToString("MMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>What installing a version would actually cost, in plain language.</summary>
public sealed record VersionDetail(
    string Id,
    string Kind,
    string Java,
    string AssetIndex,
    string Libraries,
    string ClientSize,
    string LibrarySize,
    string AssetSize,
    string TotalSize);

public partial class VersionPickerViewModel(AsobuLauncher launcher, Action<Instance> onCreated, Action onBack) : ViewModelBase
{
    private readonly List<VersionRow> _all = [];
    private CancellationTokenSource? _detailRequest;
    private bool _loaded;

    /// <summary>Which Minecraft versions each performance mod publishes for. Fetched once.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _modVersions = [];

    public ObservableCollection<VersionRow> Versions { get; } = [];

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string LatestRelease { get; set; } = "";

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool FilterReleases { get; set; } = true;
    [ObservableProperty] public partial bool FilterSnapshots { get; set; }
    [ObservableProperty] public partial bool FilterAll { get; set; }

    [ObservableProperty] public partial VersionRow? SelectedVersion { get; set; }
    [ObservableProperty] public partial VersionDetail? Detail { get; set; }
    [ObservableProperty] public partial bool IsLoadingDetail { get; set; }
    [ObservableProperty] public partial string InstanceName { get; set; } = "";

    // ---- Mod loader.

    [ObservableProperty] public partial bool UseVanilla { get; set; } = true;
    [ObservableProperty] public partial bool UseFabric { get; set; }
    [ObservableProperty] public partial bool UseForge { get; set; }
    [ObservableProperty] public partial bool UseNeoForge { get; set; }
    [ObservableProperty] public partial bool UseQuilt { get; set; }

    /// <summary>The build each loader offers for this version, or null when it offers none.</summary>
    [ObservableProperty] public partial string? FabricVersion { get; set; }
    [ObservableProperty] public partial string? ForgeVersion { get; set; }
    [ObservableProperty] public partial string? NeoForgeVersion { get; set; }
    [ObservableProperty] public partial string? QuiltVersion { get; set; }
    [ObservableProperty] public partial bool IsCheckingLoaders { get; set; }

    // ---- Optional extras.

    [ObservableProperty] public partial bool IncludePerformanceMod { get; set; }
    [ObservableProperty] public partial string PerformanceModName { get; set; } = "Sodium";
    [ObservableProperty] public partial bool CanUsePerformanceMod { get; set; }
    [ObservableProperty] public partial string PerformanceModNote { get; set; } = "";

    public bool HasNoSelection => SelectedVersion is null;
    public bool CanCreate => Detail is not null && InstanceName.Trim().Length > 0 && !IsCheckingLoaders;

    public bool FabricAvailable => FabricVersion is { Length: > 0 };
    public bool ForgeAvailable => ForgeVersion is { Length: > 0 };
    public bool NeoForgeAvailable => NeoForgeVersion is { Length: > 0 };
    public bool QuiltAvailable => QuiltVersion is { Length: > 0 };

    /// <summary>
    /// Whether any loader is chosen. The extras only exist on top of a loader, so with none
    /// picked there is nothing to offer and the whole block goes away rather than sitting there
    /// greyed out explaining itself.
    /// </summary>
    public bool HasLoader => Loader != Loaders.Vanilla;

    public string LoaderSummary => Loader switch
    {
        Loaders.Fabric => $"Fabric {FabricVersion}",
        Loaders.Forge => $"Forge {ForgeVersion}",
        Loaders.NeoForge => $"NeoForge {NeoForgeVersion}",
        Loaders.Quilt => $"Quilt {QuiltVersion}",
        _ => "Vanilla · no mod loader",
    };

    partial void OnDetailChanged(VersionDetail? value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnInstanceNameChanged(string value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnIsCheckingLoadersChanged(bool value) => OnPropertyChanged(nameof(CanCreate));

    partial void OnFabricVersionChanged(string? value) => OnLoaderAvailabilityChanged(nameof(FabricAvailable));
    partial void OnForgeVersionChanged(string? value) => OnLoaderAvailabilityChanged(nameof(ForgeAvailable));
    partial void OnNeoForgeVersionChanged(string? value) => OnLoaderAvailabilityChanged(nameof(NeoForgeAvailable));
    partial void OnQuiltVersionChanged(string? value) => OnLoaderAvailabilityChanged(nameof(QuiltAvailable));

    private void OnLoaderAvailabilityChanged(string property)
    {
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(LoaderSummary));

        // A loader might not exist for the version just clicked; don't leave its chip selected.
        // Cleared here rather than left to the radio group: these flags are independent in the
        // view model, and relying on the view to write false back would leave Loader wrong for
        // however long the group takes to notice.
        if ((UseFabric && !FabricAvailable) || (UseForge && !ForgeAvailable)
            || (UseNeoForge && !NeoForgeAvailable) || (UseQuilt && !QuiltAvailable))
        {
            UseFabric = false;
            UseForge = false;
            UseNeoForge = false;
            UseQuilt = false;
            UseVanilla = true;
        }
    }

    partial void OnUseVanillaChanged(bool value) => OnLoaderChanged();
    partial void OnUseFabricChanged(bool value) => OnLoaderChanged();
    partial void OnUseForgeChanged(bool value) => OnLoaderChanged();
    partial void OnUseNeoForgeChanged(bool value) => OnLoaderChanged();
    partial void OnUseQuiltChanged(bool value) => OnLoaderChanged();

    private void OnLoaderChanged()
    {
        OnPropertyChanged(nameof(LoaderSummary));
        OnPropertyChanged(nameof(HasLoader));

        _ = RefreshPerformanceModAsync();
    }

    private string Loader =>
        UseFabric && FabricAvailable ? Loaders.Fabric
        : UseForge && ForgeAvailable ? Loaders.Forge
        : UseNeoForge && NeoForgeAvailable ? Loaders.NeoForge
        : UseQuilt && QuiltAvailable ? Loaders.Quilt
        : Loaders.Vanilla;

    private string? LoaderVersion => Loader switch
    {
        Loaders.Fabric => FabricVersion,
        Loaders.Forge => ForgeVersion,
        Loaders.NeoForge => NeoForgeVersion,
        Loaders.Quilt => QuiltVersion,
        _ => null,
    };

    [RelayCommand]
    private void Create()
    {
        if (SelectedVersion is not { } row || InstanceName.Trim() is not { Length: > 0 } name) return;

        var loader = Loader;

        // Downloading happens on Play, so creating an instance stays instant and offline-safe —
        // the extras are recorded as wishes and fetched with everything else.
        onCreated(launcher.Instances.Create(
            name,
            row.Id,
            loader,
            LoaderVersion,
            IncludePerformanceMod && CanUsePerformanceMod ? Modrinth.PerformanceModFor(loader) : null));
    }

    /// <summary>
    /// A performance mod only makes sense on a loader, and only where the mod actually publishes
    /// a build. Both are checked rather than guessed at from a minimum version number, so this
    /// stays right as the mod adds support for new Minecraft releases.
    /// </summary>
    private async Task RefreshPerformanceModAsync()
    {
        var loader = Loader;

        if (loader == Loaders.Vanilla)
        {
            CanUsePerformanceMod = false;
            IncludePerformanceMod = false;
            return;
        }

        var project = Modrinth.PerformanceModFor(loader);
        PerformanceModName = project == Modrinth.Embeddium ? "Embeddium" : "Sodium";

        if (!_modVersions.TryGetValue(project, out var supported))
            _modVersions[project] = supported = await launcher.Modrinth.GetGameVersionsAsync(project);

        var version = SelectedVersion?.Id;
        CanUsePerformanceMod = version is not null && supported.Contains(version, StringComparer.OrdinalIgnoreCase);

        PerformanceModNote = CanUsePerformanceMod
            ? $"Renders far faster. Installed on first launch."
            : $"{PerformanceModName} has no build for {version}.";

        // Suggested rather than merely offered: on a modded instance this is what almost everyone
        // wants, and it costs one click to decline.
        IncludePerformanceMod = CanUsePerformanceMod;
    }

    [RelayCommand]
    private void Back() => onBack();

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;

        IsLoading = true;
        Error = null;
        try
        {
            var manifest = await launcher.Meta.GetManifestAsync();
            LatestRelease = manifest.Latest.Release;
            _all.AddRange(manifest.Versions.Select(v => new VersionRow(v)));
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Error = $"Could not reach Mojang: {ex.Message}";
            _loaded = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnFilterReleasesChanged(bool value) => ApplyFilter();
    partial void OnFilterSnapshotsChanged(bool value) => ApplyFilter();
    partial void OnFilterAllChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_all.Count == 0) return;

        var matches = _all.Where(row => FilterAll
            || (FilterSnapshots && row.Summary.Type == "snapshot")
            || (FilterReleases && row.Summary.IsRelease));

        var search = SearchText.Trim();
        if (search.Length > 0)
            matches = matches.Where(row => row.Id.Contains(search, StringComparison.OrdinalIgnoreCase));

        Versions.Clear();
        foreach (var row in matches) Versions.Add(row);
    }

    partial void OnSelectedVersionChanged(VersionRow? value)
    {
        OnPropertyChanged(nameof(HasNoSelection));
        if (value is not null) InstanceName = value.Id;

        _ = LoadDetailAsync(value);
        _ = LoadLoadersAsync(value);
    }

    /// <summary>
    /// Asks all three loaders at once what they have for this version. Each is allowed to answer
    /// "nothing" — Fabric starts at 1.14, NeoForge at 1.20.2 — and a stale answer for a version
    /// the user has already clicked away from is dropped.
    /// </summary>
    private async Task LoadLoadersAsync(VersionRow? row)
    {
        FabricVersion = ForgeVersion = NeoForgeVersion = QuiltVersion = null;
        if (row is null) return;

        IsCheckingLoaders = true;
        try
        {
            var fabric = launcher.Fabric.GetLatestLoaderAsync(row.Id);
            var forge = launcher.Loaders.GetForgeVersionAsync(row.Id);
            var neoForge = launcher.Loaders.GetNeoForgeVersionAsync(row.Id);
            var quilt = launcher.Quilt.GetLatestLoaderAsync(row.Id);

            // All four at once: asking in turn would make picking a version feel four times
            // slower than the slowest of them.
            await Task.WhenAll(fabric, forge, neoForge, quilt);

            if (SelectedVersion?.Id != row.Id) return;

            FabricVersion = fabric.Result;
            ForgeVersion = forge.Result;
            NeoForgeVersion = neoForge.Result;
            QuiltVersion = quilt.Result;
        }
        finally
        {
            if (SelectedVersion?.Id == row.Id) IsCheckingLoaders = false;
        }

        await RefreshPerformanceModAsync();
    }

    private async Task LoadDetailAsync(VersionRow? row)
    {
        // A fast click through the list must not let a stale response overwrite a newer one.
        _detailRequest?.Cancel();
        Detail = null;
        if (row is null) return;

        var request = new CancellationTokenSource();
        _detailRequest = request;
        IsLoadingDetail = true;
        try
        {
            var version = await launcher.Meta.GetResolvedVersionAsync(row.Id, request.Token);
            if (!request.IsCancellationRequested) Detail = Describe(row, version);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested) Error = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_detailRequest, request)) IsLoadingDetail = false;
        }
    }

    private static VersionDetail Describe(VersionRow row, VersionJson version)
    {
        var platform = RuleContext.Current;
        var libraries = version.Libraries.Where(l => RuleEvaluator.Allows(l, platform)).ToList();

        var librarySize = libraries.Sum(l => l.Downloads?.Artifact?.Size ?? 0);
        var clientSize = version.ClientJar?.Size ?? 0;
        var assetSize = version.AssetIndex?.TotalSize ?? 0;

        return new VersionDetail(
            Id: version.Id,
            Kind: row.Kind,
            // Versions older than 1.17 carry no javaVersion block; they all want Java 8.
            Java: $"Java {version.JavaVersion?.MajorVersion ?? 8}",
            AssetIndex: version.AssetIndex?.Id ?? version.Assets ?? "unknown",
            Libraries: $"{libraries.Count} of {version.Libraries.Count} needed on this PC",
            ClientSize: Format.Bytes(clientSize),
            LibrarySize: Format.Bytes(librarySize),
            AssetSize: Format.Bytes(assetSize),
            TotalSize: Format.Bytes(clientSize + librarySize + assetSize));
    }
}
