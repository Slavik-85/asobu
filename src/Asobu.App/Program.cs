using Avalonia;
using System;
using Velopack;

namespace Asobu.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // First, before anything else exists. An install, an update and an uninstall all
        // re-run this same executable with arguments that mean "do the plumbing and exit" —
        // so if a window were built first, installing Asobu would flash one up and updating
        // it would run two copies at once. Harmless and does nothing in an ordinary launch.
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
