using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Asobu.App.ViewModels;
using Asobu.App.Views;

namespace Asobu.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainViewModel();

            desktop.MainWindow = new MainWindow
            {
                DataContext = main,
            };

            // Minecraft goes when Asobu does. Everything holding a multiplayer session together —
            // the tunnel, the door, the stand-in that vouched for the players in it — lives in
            // this process, so a game left behind is one whose multiplayer has quietly stopped.
            //
            // Long enough for the game to save and close itself; a game that will not go is then
            // taken down rather than left holding the launcher open.
            desktop.ShutdownRequested += (_, _) => main.Launcher.StopGames(TimeSpan.FromSeconds(8));
        }

        base.OnFrameworkInitializationCompleted();
    }
}