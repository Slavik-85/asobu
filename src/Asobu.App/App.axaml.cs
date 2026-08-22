using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
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

            window.Closing += (_, closing) =>
            {
                if (_leaving) return;

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

            BuildTray(desktop, window);
        }

        base.OnFrameworkInitializationCompleted();
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
    private static WindowIcon TrayImage()
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
