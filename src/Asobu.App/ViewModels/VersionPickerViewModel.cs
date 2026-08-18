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

public partial class VersionPickerViewModel(AsobuLauncher launcher, Action<Instance> onCreated) : ViewModelBase
{
    private readonly List<VersionRow> _all = [];
    private CancellationTokenSource? _detailRequest;
    private bool _loaded;

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

    public bool HasNoSelection => SelectedVersion is null;
    public bool CanCreate => Detail is not null && InstanceName.Trim().Length > 0;

    partial void OnDetailChanged(VersionDetail? value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnInstanceNameChanged(string value) => OnPropertyChanged(nameof(CanCreate));

    [RelayCommand]
    private void Create()
    {
        if (SelectedVersion is not { } row || InstanceName.Trim() is not { Length: > 0 } name) return;

        // Downloading happens on Play, so creating an instance stays instant and offline-safe.
        onCreated(launcher.Instances.Create(name, row.Id));
    }

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
