using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Asobu.App.ViewModels;
using Asobu.App.Views;

namespace Asobu.App;

public partial class App : Application
{
    /// <summary>
    /// Set only by the Exit menu entry. Closing the window means "put it away"; this is the one
    /// thing that means "stop".
    /// </summary>
    private bool _leaving;

    /// <summary>
    /// How long a game gets to save and close itself before it is taken down. Long enough for
    /// Minecraft to write the world out, short enough that a stuck one does not hold the exit.
    /// </summary>
    private static readonly TimeSpan GoodbyeWait = TimeSpan.FromSeconds(8);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainViewModel();
            var window = new MainWindow { DataContext = main };

            desktop.MainWindow = window;

            // Without this, hiding the only window is the same as quitting, and Asobu would stop
            // the moment it went to the tray.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _ = FindTrayAsync();
            DesktopIntegration.Run(main.Launcher);

            window.Closing += (_, closing) =>
            {
                if (_leaving) return;

                // Nowhere to hide. Closing means quitting then, as it does for every other app on
                // that desktop — except while a game is running, which quitting would take with
                // it; minimised keeps both.
                if (!_hasTray && window.IsVisible)
                {
                    if (main.InstancesPage.IsRunning)
                    {
                        closing.Cancel = true;
                        window.WindowState = WindowState.Minimized;
                        return;
                    }

                    _leaving = true;
                    Dispatcher.UIThread.Post(() => desktop.Shutdown());
                    return;
                }

                // Already in the tray, and something is still asking it to close. Nobody clicked
                // anything, because there is nothing on screen to click: this is the system or a
                // task manager asking, and the honest answer to that is to go.
                if (!window.IsVisible)
                {
                    _leaving = true;
                    main.Launcher.StopGames(GoodbyeWait);
                    return;
                }

                closing.Cancel = true;
                window.Hide();

                // Said out loud, because a window that vanishes without quitting is a program
                // somebody will look for in Task Manager to find out what happened to it.
                TrayToast.Show(window, () => Reopen(window));
            };

            // Both, because they fire on different exits and neither covers the other.
            // ShutdownRequested is the platform asking, as when the session ends. Exit is the one
            // that follows Shutdown(), which is what the tray's Exit calls: hooking only the
            // first meant choosing Exit left the game running, which is the whole thing this is
            // here to prevent.
            desktop.ShutdownRequested += (_, _) => main.Launcher.StopGames(GoodbyeWait);
            desktop.Exit += (_, _) => main.Launcher.StopGames(GoodbyeWait);

            // Someone starting Asobu again while it is in the tray means "show me Asobu",
            // which is the same request the tray's own Open makes.
            SingleInstance.Listen(() => Dispatcher.UIThread.Post(() => Reopen(window)));

            // Avalonia's Linux tray claims its D-Bus name with nothing around the call, so a bus
            // that says no — a Flatpak not allowed that name — took the whole app down a second
            // after it opened. A tray that failed is no tray: closing then quits rather than
            // hiding the window somewhere nobody can get it back from.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                if (e.Exception.StackTrace?.Contains("DBusTrayIconImpl") != true) return;
                _trayFailed = true;
                _hasTray = false;
                e.Handled = true;
            };

            BuildTray(desktop, window);

            // After the window and tray exist, since the theme recolours their icons too. Before
            // either is shown, so nothing is ever seen in the wrong colours.
            Theming.Apply(main.Launcher.Settings);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Whether there is a tray to hide in. Always on Windows. On Linux only when the desktop runs
    /// a StatusNotifierItem host — KDE, Ubuntu's GNOME and most others do, stock GNOME does not —
    /// and without one a window hidden "to the tray" was simply gone, with Asobu still running
    /// and nothing on screen to bring it back. Assumed absent until the desktop has answered.
    /// </summary>
    private static bool _hasTray = !OperatingSystem.IsLinux();

    /// <summary>Set when the tray failed to register, which outranks the desktop having one.</summary>
    private static bool _trayFailed;

    private static async Task FindTrayAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            var services = await Tmds.DBus.Protocol.DBusConnection.Session.ListServicesAsync();
            _hasTray = !_trayFailed && Array.IndexOf(services, "org.kde.StatusNotifierWatcher") >= 0;
        }
        catch (Exception)
        {
            // No session bus to ask, which means no tray to hide in either.
        }
    }

    private void BuildTray(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        var launcher = (window.DataContext as MainViewModel)?.Launcher;

        var open = new NativeMenuItem("Open");
        open.Click += (_, _) => Reopen(window);

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            _leaving = true;

            // Before Shutdown rather than after, and not left to the lifetime events. Which of
            // those fires depends on how the exit was asked for, and the game outliving Asobu is
            // exactly the thing this menu entry exists to avoid.
            launcher?.StopGames(GoodbyeWait);

            desktop.Shutdown();
        };

        var tray = new TrayIcon
        {
            Icon = TrayImage(),
            ToolTipText = "Asobu",
            Menu = [open, exit],
            IsVisible = true,
        };

        // A left click reopens on Windows. Several Linux desktops only ever offer the menu, which
        // is why Open is in it as well rather than being a duplicate of the obvious gesture.
        tray.Clicked += (_, _) => Reopen(window);

        // Held by the application so the icon lives as long as Asobu does.
        TrayIcon.SetIcons(this, [tray]);
    }

    /// <summary>
    /// Windows wants an .ico and reads the sizes packed inside it. Everywhere else a PNG is what
    /// gets asked for, and handing over an .ico gives a blank square.
    /// </summary>
    internal static WindowIcon TrayImage()
    {
        var file = OperatingSystem.IsWindows() ? "asobu.ico" : "asobu.png";
        return new WindowIcon(AssetLoader.Open(new Uri($"avares://Asobu.App/Assets/{file}")));
    }

    private static void Reopen(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
