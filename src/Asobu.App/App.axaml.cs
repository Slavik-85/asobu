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

            desktop.ShutdownRequested += (_, _) => main.Launcher.StopGames(TimeSpan.FromSeconds(8));

            BuildTray(desktop, window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void BuildTray(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        var open = new NativeMenuItem("Open");
        open.Click += (_, _) => Reopen(window);

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            _leaving = true;
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
