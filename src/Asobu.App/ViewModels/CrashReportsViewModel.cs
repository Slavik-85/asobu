using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Diagnostics;
using Asobu.Core.Instances;
using Asobu.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>
/// A suspect plus the one action worth offering about it. Turning a mod off is a rename, so it
/// costs nothing to try and nothing to undo — which is exactly the right weight for a guess.
/// </summary>
public partial class CrashSuspectViewModel(CrashSuspect suspect, ModEntry? mod) : ViewModelBase
{
    public string Name { get; } = suspect.Name;
    public string FileName { get; } = suspect.FileName;
    public string ConfidenceLabel { get; } = suspect.ConfidenceLabel;
    public IReadOnlyList<string> Evidence { get; } = suspect.Evidence;

    public bool HasEvidence => Evidence.Count > 0;

    [ObservableProperty] public partial bool IsDisabled { get; set; } = mod is { Enabled: false };
    [ObservableProperty] public partial string? Notice { get; set; }

    public bool CanDisable => mod is { Enabled: true } && !IsDisabled;

    partial void OnIsDisabledChanged(bool value) => OnPropertyChanged(nameof(CanDisable));

    [RelayCommand]
    private void Disable()
    {
        if (mod is not { Enabled: true } entry) return;

        try
        {
            ModScanner.SetEnabled(entry, false);
            IsDisabled = true;
            Notice = "Turned off. Launch again to see if it was this one.";
        }
        catch (Exception ex)
        {
            Notice = $"Couldn't turn it off: {ex.Message}";
        }
    }
}

public partial class CrashReportsViewModel(AsobuLauncher launcher, Action onBack) : ViewModelBase
{
    private CancellationTokenSource? _contentRequest;

    /// <summary>Scanning the mods folder opens every jar, so it happens once per visit.</summary>
    private IReadOnlyList<ModEntry> _mods = [];

    public ObservableCollection<CrashReportEntry> Reports { get; } = [];
    public ObservableCollection<CrashSuspectViewModel> Suspects { get; } = [];

    [ObservableProperty] public partial Instance? Instance { get; set; }
    [ObservableProperty] public partial CrashReportEntry? Selected { get; set; }
    [ObservableProperty] public partial string? Content { get; set; }
    [ObservableProperty] public partial bool IsLoadingContent { get; set; }
    [ObservableProperty] public partial CrashAnalysis? Analysis { get; set; }

    public bool IsEmpty => Reports.Count == 0;
    public bool HasSelection => Selected is not null;
    public bool HasVerdict => Analysis is { HasVerdict: true };
    public bool HasSuspects => Analysis is { HasSuspects: true };

    /// <summary>Opens the page for an instance, listing its crash reports and past sessions.</summary>
    public void Load(Instance instance)
    {
        Instance = instance;
        Content = null;
        Selected = null;
        Analysis = null;
        Suspects.Clear();

        Reports.Clear();
        foreach (var entry in CrashReports.List(launcher.Paths, instance)) Reports.Add(entry);

        OnPropertyChanged(nameof(IsEmpty));
        _ = LoadModsAsync(instance);
    }

    private async Task LoadModsAsync(Instance instance)
    {
        var directory = ModScanner.ModsDirectory(launcher.Paths, instance.Folder);
        var mods = await Task.Run(() => ModScanner.Scan(directory));

        if (Instance?.Id != instance.Id) return;
        _mods = mods;

        // The report may already be on screen, read before the scan finished — analysing it
        // against an empty mod list would have found nothing to accuse.
        if (Content is { Length: > 0 } text) await AnalyseAsync(text);
    }

    partial void OnSelectedChanged(CrashReportEntry? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        _ = LoadContentAsync(value);
    }

    partial void OnAnalysisChanged(CrashAnalysis? value)
    {
        OnPropertyChanged(nameof(HasVerdict));
        OnPropertyChanged(nameof(HasSuspects));
    }

    private async Task LoadContentAsync(CrashReportEntry? entry)
    {
        _contentRequest?.Cancel();
        Content = null;
        Analysis = null;
        Suspects.Clear();
        if (entry is null) return;

        var request = new CancellationTokenSource();
        _contentRequest = request;
        IsLoadingContent = true;
        try
        {
            var text = await CrashReports.ReadAsync(entry.Path, request.Token);
            if (request.IsCancellationRequested) return;

            Content = text;
            await AnalyseAsync(text);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested) Content = $"Couldn't read this file: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_contentRequest, request)) IsLoadingContent = false;
        }
    }

    /// <summary>
    /// Off the UI thread: the patterns run over the whole report, which for a launch log is up to
    /// 300 KB of text.
    /// </summary>
    private async Task AnalyseAsync(string text)
    {
        var mods = _mods;
        var analysis = await Task.Run(() => CrashAnalyzer.Analyze(text, mods));

        if (!ReferenceEquals(text, Content)) return;

        Suspects.Clear();
        foreach (var suspect in analysis.Suspects)
        {
            var mod = mods.FirstOrDefault(m => m.FileName == suspect.FileName);
            Suspects.Add(new CrashSuspectViewModel(suspect, mod));
        }

        Analysis = analysis;
    }

    [RelayCommand]
    private void Back() => onBack();

    [RelayCommand]
    private void Refresh()
    {
        if (Instance is { } instance) Load(instance);
    }
}
