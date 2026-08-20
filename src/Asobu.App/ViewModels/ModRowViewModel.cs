using System;
using System.IO;
using Asobu.Core;
using Asobu.Core.Mods;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Asobu.App.ViewModels;

public partial class ModRowViewModel : ViewModelBase
{
    private ModEntry _entry;
    private bool _applying;

    public ModRowViewModel(ModEntry entry)
    {
        _entry = entry;
        IsEnabled = entry.Enabled;
        Icon = LoadIcon(entry.IconPng);
    }

    public string Name => _entry.Name;
    public string Author => _entry.Author;
    public string SizeLabel => _entry.SizeLabel;

    /// <summary>Where the jar is, which is how an update is matched back to the row that wants it.</summary>
    public string Path => _entry.Path;
    public Bitmap? Icon { get; }

    /// <summary>True when the jar has no embedded icon, so the view can show a placeholder tile.</summary>
    public bool HasIcon => Icon is not null;

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    /// <summary>
    /// False for a world, which the game finds by looking in saves/ rather than by name, so
    /// renaming one aside would not disable it — and a switch that does nothing is worse than
    /// no switch.
    /// </summary>
    public bool CanToggle { get; init; } = true;
    [ObservableProperty] public partial string? Error { get; set; }

    /// <summary>A newer build, once the check has come back. Null means up to date, or unknown.</summary>
    [ObservableProperty] public partial AsobuLauncher.ModUpdate? Update { get; set; }

    [ObservableProperty] public partial bool IsUpdating { get; set; }
    [ObservableProperty] public partial bool IsUpdated { get; set; }

    /// <summary>
    /// False while the update is running, as well as after. The three states share one cell, so
    /// a button that stayed put during its own work would sit underneath the word "Updating".
    /// </summary>
    public bool HasUpdate => Update is { CanApply: true } && !IsUpdated && !IsUpdating;

    /// <summary>The file that would replace this one, so the offer is not a leap of faith.</summary>
    public string UpdateLabel => Update is { } update ? update.ToFileName : "";

    partial void OnUpdateChanged(AsobuLauncher.ModUpdate? value) => OnPropertyChanged(nameof(HasUpdate));

    partial void OnIsUpdatedChanged(bool value) => OnPropertyChanged(nameof(HasUpdate));

    partial void OnIsUpdatingChanged(bool value) => OnPropertyChanged(nameof(HasUpdate));

    partial void OnIsEnabledChanged(bool value)
    {
        // Guard the write-back we do ourselves when a rename fails and we revert the switch.
        if (_applying) return;

        try
        {
            var path = ModScanner.SetEnabled(_entry, value);
            _entry = _entry with { Path = path, Enabled = value };
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The game usually holds its jars open while running, so this is a real case.
            Error = ex.Message;
            _applying = true;
            IsEnabled = !value;
            _applying = false;
        }
    }

    /// <summary>
    /// Re-points the row at the file that replaced its jar, and drops everything that was true
    /// only of the old one. Called the moment an update finishes, so the offer to update leaves
    /// with the build it was offering to replace rather than lingering as a stale row.
    ///
    /// The icon is kept rather than re-read: a version bump almost never changes it, and
    /// rebuilding the bitmap would flicker the tile for nothing.
    /// </summary>
    public void Adopt(ModEntry entry)
    {
        _entry = entry;

        // Through the guard: this is following the file, not asking for it to be renamed.
        _applying = true;
        IsEnabled = entry.Enabled;
        _applying = false;

        Update = null;
        IsUpdated = false;
        Error = null;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Author));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(Path));
    }

    private static Bitmap? LoadIcon(byte[]? png)
    {
        if (png is null || png.Length == 0) return null;

        try
        {
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            // A mod can declare an icon path that isn't a decodable image; not worth failing over.
            return null;
        }
    }
}
