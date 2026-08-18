using System;
using System.IO;
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
    public Bitmap? Icon { get; }

    /// <summary>True when the jar has no embedded icon, so the view can show a placeholder tile.</summary>
    public bool HasIcon => Icon is not null;

    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

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
