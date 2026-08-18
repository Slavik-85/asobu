using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Diagnostics;
using Asobu.Core.Instances;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

public partial class CrashReportsViewModel(AsobuLauncher launcher, Action onBack) : ViewModelBase
{
    private CancellationTokenSource? _contentRequest;

    public ObservableCollection<CrashReportEntry> Reports { get; } = [];

    [ObservableProperty] public partial Instance? Instance { get; set; }
    [ObservableProperty] public partial CrashReportEntry? Selected { get; set; }
    [ObservableProperty] public partial string? Content { get; set; }
    [ObservableProperty] public partial bool IsLoadingContent { get; set; }

    public bool IsEmpty => Reports.Count == 0;
    public bool HasSelection => Selected is not null;

    public void Load(Instance instance)
    {
        Instance = instance;
        Content = null;
        Selected = null;

        Reports.Clear();
        foreach (var entry in CrashReports.List(launcher.Paths, instance)) Reports.Add(entry);

        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedChanged(CrashReportEntry? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        _ = LoadContentAsync(value);
    }

    private async Task LoadContentAsync(CrashReportEntry? entry)
    {
        _contentRequest?.Cancel();
        Content = null;
        if (entry is null) return;

        var request = new CancellationTokenSource();
        _contentRequest = request;
        IsLoadingContent = true;
        try
        {
            var text = await CrashReports.ReadAsync(entry.Path, request.Token);
            if (!request.IsCancellationRequested) Content = text;
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

    [RelayCommand]
    private void Back() => onBack();

    [RelayCommand]
    private void Refresh()
    {
        if (Instance is { } instance) Load(instance);
    }
}
