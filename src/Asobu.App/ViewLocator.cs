using Asobu.App.ViewModels;
using Asobu.App.Views;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Asobu.App;

/// <summary>
/// Builds the view for a view model.
///
/// Written out rather than looked up. The template every Avalonia project starts with takes the
/// view model's type name, swaps "ViewModel" for "View" and resolves that string through
/// reflection — which works, but costs a type lookup and an Activator call on every page change,
/// cannot be trimmed (the types are only ever named in a string, so a trimmer removes them and
/// the app shows "Not Found" at runtime), and turns a renamed class into a blank screen instead
/// of a build error.
///
/// Ten lines of switch buys back all three.
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        InstancesViewModel => new InstancesView(),
        ServersViewModel => new ServersView(),
        ExploreViewModel => new ExploreView(),
        BrowseViewModel => new BrowseView(),
        ModPageViewModel => new ModPageView(),
        VersionPickerViewModel => new VersionPickerView(),
        AccountsViewModel => new AccountsView(),
        SettingsViewModel => new SettingsView(),
        CrashReportsViewModel => new CrashReportsView(),
        InstallPickerViewModel => new InstallPickerView(),
        PackInstallViewModel => new PackInstallView(),

        // A view model with no view is a mistake worth seeing rather than an empty panel.
        not null => new TextBlock { Text = "No view for " + param.GetType().Name },
        _ => null,
    };

    public bool Match(object? data) => data is ViewModelBase;
}
