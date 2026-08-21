using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Asobu.Core;
using Asobu.Core.Accounts;
using Asobu.Core.Java;
using Asobu.Core.Launch;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

public sealed record GpuOption(GpuPreference Value, string Label, string Detail);

public sealed record JavaOption(string Value, string Label, string Detail);

public sealed record SignInOption(AuthMethod Value, string Label, string Detail);

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AsobuLauncher _launcher;
    private readonly Action _replayIntro;
    private readonly Action _replayTour;
    private bool _loading;

    public SettingsViewModel(
        AsobuLauncher launcher, UpdateViewModel updates, Action replayIntro, Action replayTour)
    {
        _launcher = launcher;
        Updates = updates;
        _replayIntro = replayIntro;
        _replayTour = replayTour;

        MaximumMemoryMb = Math.Max(2048, LauncherSettings.SystemMemoryMb());
        DetectedGpus = string.Join(", ", GpuPreferences.Detect());

        SignInOptions =
        [
            new(AuthMethod.DeviceCode, "Device code (works now)",
                "Uses the Minecraft launcher's own public app id, so no registration is needed. The consent screen will say Minecraft Launcher, not Asobu."),
            new(AuthMethod.Registered, "Asobu's own app registration",
                "The honest route. Needs the client id below, approved by Mojang."),
        ];

        GpuOptions =
        [
            new(GpuPreference.HighPerformance, "High performance", "Use the dedicated GPU. Best for laptops."),
            new(GpuPreference.PowerSaving, "Power saving", "Use the integrated GPU. Longer battery life."),
            new(GpuPreference.Auto, "Let Windows decide", "Leave the system default alone."),
        ];

        Load();
    }

    public int MaximumMemoryMb { get; }

    /// <summary>
    /// The GPU preference is a Windows graphics setting written to the registry, and there is
    /// no equivalent to offer elsewhere — Linux picks its GPU through PRIME or the driver's own
    /// configuration, neither of which a launcher should be reaching into. So the card is not
    /// shown rather than shown and inert.
    /// </summary>
    public bool ShowGraphics => OperatingSystem.IsWindows();

    public string DetectedGpus { get; }
    public IReadOnlyList<GpuOption> GpuOptions { get; }
    public IReadOnlyList<SignInOption> SignInOptions { get; }
    public ObservableCollection<JavaOption> JavaOptions { get; } = [];

    [ObservableProperty] public partial bool AutomaticMemory { get; set; }
    [ObservableProperty] public partial int MinMemoryMb { get; set; }
    [ObservableProperty] public partial int MaxMemoryMb { get; set; }
    [ObservableProperty] public partial GpuOption? Gpu { get; set; }
    [ObservableProperty] public partial JavaOption? Java { get; set; }
    [ObservableProperty] public partial string ExtraJvmArguments { get; set; } = "";
    [ObservableProperty] public partial SignInOption? SignIn { get; set; }
    [ObservableProperty] public partial string MicrosoftClientId { get; set; } = "";
    [ObservableProperty] public partial string CurseForgeApiKey { get; set; } = "";
    [ObservableProperty] public partial string? Notice { get; set; }

    public string MinMemoryLabel => $"{MinMemoryMb} MB";
    public string MaxMemoryLabel => $"{MaxMemoryMb} MB";
    public string DataFolder => _launcher.Paths.Root;

    /// <summary>
    /// Whether this build already carries a key decides what there is to say here: with one,
    /// pasting is an override; without, it is the only way to switch CurseForge on.
    /// </summary>
    /// <summary>
    /// Whether to offer the Azure app settings at all.
    ///
    /// Device code needs no configuration whatsoever, which is the route every release takes, so
    /// for everyone using Asobu this card is a box of questions with no answers behind it. Prism
    /// compiles its Microsoft client id in and offers no setting for it either.
    ///
    /// It reappears the moment a client id is present, so setting one in settings.json brings
    /// the controls back to manage it — which is how it will be turned on once Mojang approves
    /// Asobu's own registration.
    /// </summary>
    public bool ShowMicrosoftSetup => MicrosoftClientId is { Length: > 0 };

    /// <summary>
    /// The updater, shown here as well as in the sidebar. Handed in rather than made here: the
    /// sidebar's button and this card have to be looking at the same one, or the page would
    /// offer to check for an update the sidebar has already downloaded.
    /// </summary>
    public UpdateViewModel Updates { get; }

    /// <summary>
    /// Whether to offer the key box at all.
    ///
    /// A release carries its own key, so the box would be an invitation to break something that
    /// already works — and a field showing a key is a field someone can read over your shoulder
    /// or screenshot into a bug report. A build made from source without a key still needs it,
    /// and for that build it is the only way to switch CurseForge on, so it stays.
    /// </summary>
    public bool ShowCurseForgeKey => !BuildConfig.HasCurseForgeKey;

    public string CurseForgeStatus => BuildConfig.HasCurseForgeKey
        ? "This build carries a CurseForge key, so browsing already works. Paste your own below only to use it instead."
        : "CurseForge issue a key to each launcher, so one has to be built into the release or pasted here. Making your own is free. Modrinth needs nothing and works already.";

    public string CurseForgeKeyPlaceholder => BuildConfig.HasCurseForgeKey
        ? "Using this build's key"
        : "Paste your CurseForge API key";

    public void Load()
    {
        _loading = true;

        var settings = _launcher.Settings;
        AutomaticMemory = settings.AutomaticMemory;
        MinMemoryMb = settings.MinMemoryMb;
        MaxMemoryMb = settings.MaxMemoryMb;
        ExtraJvmArguments = settings.ExtraJvmArguments ?? "";
        MicrosoftClientId = settings.MicrosoftClientId ?? "";
        CurseForgeApiKey = settings.CurseForgeApiKey ?? "";
        Gpu = GpuOptions.FirstOrDefault(o => o.Value == settings.Gpu) ?? GpuOptions[0];
        SignIn = SignInOptions.FirstOrDefault(o => o.Value == settings.MicrosoftSignIn) ?? SignInOptions[0];

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

    partial void OnAutomaticMemoryChanged(bool value) => Save();
    partial void OnSignInChanged(SignInOption? value) => Save();
    partial void OnGpuChanged(GpuOption? value) => Save();
    partial void OnJavaChanged(JavaOption? value) => Save();
    partial void OnExtraJvmArgumentsChanged(string value) => Save();
    partial void OnMicrosoftClientIdChanged(string value) => Save();
    partial void OnCurseForgeApiKeyChanged(string value) => Save();

    private void Save()
    {
        if (_loading) return;

        var settings = _launcher.Settings;
        settings.AutomaticMemory = AutomaticMemory;
        settings.MinMemoryMb = MinMemoryMb;
        settings.MaxMemoryMb = MaxMemoryMb;
        settings.Gpu = Gpu?.Value ?? GpuPreference.HighPerformance;
        settings.JavaSelection = Java?.Value ?? "auto";
        settings.ExtraJvmArguments = string.IsNullOrWhiteSpace(ExtraJvmArguments) ? null : ExtraJvmArguments.Trim();
        settings.MicrosoftSignIn = SignIn?.Value ?? AuthMethod.DeviceCode;
        settings.MicrosoftClientId = string.IsNullOrWhiteSpace(MicrosoftClientId) ? null : MicrosoftClientId.Trim();
        settings.CurseForgeApiKey = string.IsNullOrWhiteSpace(CurseForgeApiKey) ? null : CurseForgeApiKey.Trim();

        _launcher.SaveSettings();
        Notice = "Saved";
    }

    [RelayCommand] private void GetCurseForgeKey() =>
        AsobuLauncher.OpenUrl("https://console.curseforge.com/?#/api-keys");

    [RelayCommand] private void OpenDataFolder() => AsobuLauncher.OpenFolder(_launcher.Paths.Root);
    [RelayCommand] private void OpenInstancesFolder() => AsobuLauncher.OpenFolder(_launcher.Paths.Instances);
    [RelayCommand] private void OpenLogsFolder() => AsobuLauncher.OpenFolder(_launcher.Paths.Logs);

    /// <summary>
    /// Plays the welcome again from the top. Owned by MainViewModel because the welcome covers
    /// the whole window, which this page is only a part of.
    /// </summary>
    [RelayCommand]
    private void ReplayIntro() => _replayIntro();

    /// <summary>Runs the tour again, which takes you to the page its stops are on.</summary>
    [RelayCommand]
    private void ReplayTour() => _replayTour();
}
