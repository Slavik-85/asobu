using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Asobu.App.Views;

/// <summary>
/// The small card that appears when Asobu goes to the tray, so closing the window does not look
/// like quitting.
///
/// Drawn by Asobu rather than handed to the operating system. A Windows toast wants a packaged
/// application identity and a Linux one wants a notification daemon that may not be running, so
/// the one thing both are certain to manage is a window.
/// </summary>
public partial class TrayToast : Window
{
    /// <summary>Long enough to read twice, short enough not to sit in the corner.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private static TrayToast? _showing;

    private readonly Action _open;
    private readonly DispatcherTimer _goAway;

    public TrayToast() : this(() => { })
    {
    }

    private TrayToast(Action open)
    {
        InitializeComponent();

        _open = open;
        _goAway = new DispatcherTimer { Interval = Lifetime };
        _goAway.Tick += (_, _) => Dismiss();
    }

    /// <summary>
    /// Puts one in the corner of the screen the window was last on. A second call replaces the
    /// first rather than stacking, since they would both say the same thing.
    /// </summary>
    public static void Show(Window near, Action open)
    {
        _showing?.Dismiss();

        var toast = new TrayToast(open);
        _showing = toast;

        var area = (near.Screens.ScreenFromWindow(near) ?? near.Screens.Primary)?.WorkingArea;
        if (area is { } corner)
        {
            var scale = near.RenderScaling;
            toast.Position = new Avalonia.PixelPoint(
                corner.X + corner.Width - (int)(toast.Width * scale) - (int)(16 * scale),
                corner.Y + corner.Height - (int)(toast.Height * scale) - (int)(16 * scale));
        }

        toast.Show();
        toast._goAway.Start();
    }

    private void Toast_Pressed(object? sender, PointerPressedEventArgs e)
    {
        _open();
        Dismiss();
    }

    private void Dismiss()
    {
        _goAway.Stop();
        if (ReferenceEquals(_showing, this)) _showing = null;

        Close();
    }
}
