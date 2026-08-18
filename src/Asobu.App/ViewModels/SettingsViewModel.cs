using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Asobu.Core;
using Asobu.Core.Java;
using Asobu.Core.Launch;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

public sealed record GpuOption(GpuPreference Value, string Label, string Detail);

public sealed record JavaOption(string Value, string Label, string Detail);

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AsobuLauncher _launcher;
    private bool _loading;

    public SettingsViewModel(AsobuLauncher launcher)
    {
        _launcher = launcher;

        MaximumMemoryMb = Math.Max(2048, LauncherSettings.SystemMemoryMb());
        DetectedGpus = string.Join(", ", GpuPreferences.Detect());

        GpuOptions =
        [
            new(GpuPreference.HighPerformance, "High performance", "Use the dedicated GPU. Best for laptops."),
            new(GpuPreference.PowerSaving, "Power saving", "Use the integrated GPU. Longer battery life."),
            new(GpuPreference.Auto, "Let Windows decide", "Leave the system default alone."),
        ];

        Load();
    }

    public int MaximumMemoryMb { get; }
    public string DetectedGpus { get; }
    public IReadOnlyList<GpuOption> GpuOptions { get; }
    public ObservableCollection<JavaOption> JavaOptions { get; } = [];

    [ObservableProperty] public partial int MinMemoryMb { get; set; }
    [ObservableProperty] public partial int MaxMemoryMb { get; set; }
    [ObservableProperty] public partial GpuOption? Gpu { get; set; }
    [ObservableProperty] public partial JavaOption? Java { get; set; }
    [ObservableProperty] public partial string ExtraJvmArguments { get; set; } = "";
    [ObservableProperty] public partial string MicrosoftClientId { get; set; } = "";
    [ObservableProperty] public partial string? Notice { get; set; }

    public string MinMemoryLabel => $"{MinMemoryMb} MB";
    public string MaxMemoryLabel => $"{MaxMemoryMb} MB";
    public string DataFolder => _launcher.Paths.Root;

    public void Load()
    {
        _loading = true;

        var settings = _launcher.Settings;
        MinMemoryMb = settings.MinMemoryMb;
        MaxMemoryMb = settings.MaxMemoryMb;
        ExtraJvmArguments = settings.ExtraJvmArguments ?? "";
        MicrosoftClientId = settings.MicrosoftClientId ?? "";
        Gpu = GpuOptions.FirstOrDefault(o => o.Value == settings.Gpu) ?? GpuOptions[0];

        RefreshJavaOptions();
        Java = JavaOptions.FirstOrDefault(o => o.Value == settings.JavaSelection) ?? JavaOptions[0];

        _loading = false;
    }

    [RelayCommand]
    private void RefreshJavaOptions()
    {
        var current = Java?.Value;

        JavaOptions.Clear();
        JavaOptions.Add(new JavaOption("auto", "Automatic (recommended)",
            "Asobu downloads the exact runtime each Minecraft version asks for."));

        foreach (var installation in JavaManager.DetectSystemJava())
            JavaOptions.Add(new JavaOption(installation.ExecutablePath, $"Java {installation.Major}", installation.Source));

        // Keep a custom path the user typed into settings.json visible in the list.
        var configured = _launcher.Settings.JavaSelection;
        if (configured is not "auto" && configured.Length > 0 && JavaOptions.All(o => o.Value != configured))
            JavaOptions.Add(new JavaOption(configured, "Custom", configured));

        Java = JavaOptions.FirstOrDefault(o => o.Value == current) ?? JavaOptions[0];
    }

    partial void OnMinMemoryMbChanged(int value)
    {
        OnPropertyChanged(nameof(MinMemoryLabel));
        if (MinMemoryMb > MaxMemoryMb) MaxMemoryMb = MinMemoryMb;
        Save();
    }

    partial void OnMaxMemoryMbChanged(int value)
    {
        OnPropertyChanged(nameof(MaxMemoryLabel));
        if (MaxMemoryMb < MinMemoryMb) MinMemoryMb = MaxMemoryMb;
        Save();
    }

    partial void OnGpuChanged(GpuOption? value) => Save();
    partial void OnJavaChanged(JavaOption? value) => Save();
    partial void OnExtraJvmArgumentsChanged(string value) => Save();
    partial void OnMicrosoftClientIdChanged(string value) => Save();

    private void Save()
    {
        if (_loading) return;

        var settings = _launcher.Settings;
        settings.MinMemoryMb = MinMemoryMb;
        settings.MaxMemoryMb = MaxMemoryMb;
        settings.Gpu = Gpu?.Value ?? GpuPreference.HighPerformance;
        settings.JavaSelection = Java?.Value ?? "auto";
        settings.ExtraJvmArguments = string.IsNullOrWhiteSpace(ExtraJvmArguments) ? null : ExtraJvmArguments.Trim();
        settings.MicrosoftClientId = string.IsNullOrWhiteSpace(MicrosoftClientId) ? null : MicrosoftClientId.Trim();

        _launcher.SaveSettings();
        Notice = "Saved";
    }

    [RelayCommand] private void OpenDataFolder() => AsobuLauncher.OpenFolder(_launcher.Paths.Root);
    [RelayCommand] private void OpenInstancesFolder() => AsobuLauncher.OpenFolder(_launcher.Paths.Instances);
    [RelayCommand] private void OpenLogsFolder() => AsobuLauncher.OpenFolder(_launcher.Paths.Logs);
}
