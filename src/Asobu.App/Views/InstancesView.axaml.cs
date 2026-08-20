using Avalonia.Threading;
using Avalonia.Input;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Asobu.App.ViewModels;
using Asobu.Core.Instances;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Asobu.App.Views;

public partial class InstancesView : UserControl
{
    private InstancesViewModel? _observed;

    public InstancesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe(DataContext as InstancesViewModel);
    }

    /// <summary>
    /// The sheet's ScrollViewer keeps whatever offset it had last time, so a second visit would
    /// open part-way down the page. Reset it whenever the sheet is opened.
    /// </summary>
    private void Observe(InstancesViewModel? viewModel)
    {
        if (ReferenceEquals(_observed, viewModel)) return;

        if (_observed is not null) _observed.PropertyChanged -= OnViewModelPropertyChanged;
        _observed = viewModel;
        if (_observed is not null) _observed.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstancesViewModel.IsDetailOpen)
            && sender is InstancesViewModel { IsDetailOpen: true })
        {
            DetailScroll.ScrollToHome();
        }

        // Opening the log lands at the newest line, which is the one worth seeing.
        if (e.PropertyName == nameof(InstancesViewModel.IsLogOpen)
            && sender is InstancesViewModel { IsLogOpen: true })
        {
            ScrollLogToEnd();
        }

        if (e.PropertyName == nameof(InstancesViewModel.LogLines)) FollowLogTail();
    }

    /// <summary>
    /// Follows the output as it arrives, but only when the view is already at the bottom. Anyone
    /// who has scrolled up is reading something, and being yanked back down four times a second
    /// would make the log unreadable while the game is running — which is exactly when it matters.
    /// </summary>
    private void FollowLogTail()
    {
        if (this.FindControl<ScrollViewer>("LogScroll") is not { } scroller) return;

        var fromBottom = scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y;
        if (fromBottom > 48) return;

        ScrollLogToEnd();
    }

    /// <summary>
    /// Posted rather than called: the new text has not been measured yet, so scrolling now would
    /// reach the bottom the log had a moment ago rather than the one it has.
    /// </summary>
    private void ScrollLogToEnd() => Dispatcher.UIThread.Post(
        () => this.FindControl<ScrollViewer>("LogScroll")?.ScrollToEnd(),
        DispatcherPriority.Background);

    private void InstanceCard_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Instance instance } && DataContext is InstancesViewModel vm)
            vm.OpenInstanceCommand.Execute(instance);
    }

    private void CardPlay_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Instance instance } && DataContext is InstancesViewModel vm)
            vm.QuickPlayCommand.Execute(instance);

        // Click bubbles, and the card itself is a Button listening for it — without this the
        // instance page would open behind the launch.
        e.Handled = true;
    }

    /// <summary>
    /// The view owns the file dialogs; the view model only hears about the path. Nothing is
    /// copied onto disk until the edit is saved.
    /// </summary>
    private async void PickIcon_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel vm) return;
        if (await PickImageAsync("Choose an icon") is { } path) vm.StageCustomIcon(path);
    }

    private async void PickBanner_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel vm) return;
        if (await PickImageAsync("Choose a banner") is { } path) vm.StageCustomBanner(path);
    }

    private async Task<string?> PickImageAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif"] },
            },
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    /// <summary>
    /// The import pickers follow the icon picker's split: the view owns the dialog, the view
    /// model only ever hears about a path.
    /// </summary>
    private async void ImportPickFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a modpack or instance file",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Modpacks and instances") { Patterns = ["*.mrpack", "*.zip"] },
            },
        });

        if (files.Count > 0) await vm.ImportFromFileAsync(files[0].Path.LocalPath);
    }

    private async void ImportPickFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an instance folder",
            AllowMultiple = false,
        });

        if (folders.Count > 0) await vm.ImportFromFolderAsync(folders[0].Path.LocalPath);
    }

    /// <summary>
    /// For a download that landed somewhere nothing is watching. The name is not checked against
    /// the expected one — they pointed at the file, which is a clearer statement of intent than
    /// any filename match.
    /// </summary>
    private async void LocateBlocked_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BlockedDownloadRow row }) return;
        if (DataContext is not InstancesViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Find {row.FileName}",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Mod files") { Patterns = ["*.jar", "*.zip"] },
            },
        });

        if (files.Count > 0) vm.AcceptChosenFile(row, files[0].Path.LocalPath);
    }

    /// <summary>
    /// Files the person already has. Multiple at once: someone moving a folder of mods over
    /// should not have to do it one at a time.
    /// </summary>
    private async void AddLocalFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = vm.AddLocalTitle,
            AllowMultiple = true,
            FileTypeFilter = new List<FilePickerFileType>
            {
                // Everything takes either, and works out which it was by looking inside.
                new("Content")
                {
                    Patterns = vm.AcceptsAnyFile ? ["*.jar", "*.zip"] : [vm.LocalFilePattern],
                },
            },
        });

        if (files.Count > 0) await vm.AddLocalContentAsync([.. files.Select(f => f.Path.LocalPath)]);
    }

    /// <summary>For a world, or a pack someone keeps unzipped.</summary>
    private async void AddLocalFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = vm.AddLocalTitle,
            AllowMultiple = true,
        });

        if (folders.Count > 0) await vm.AddLocalContentAsync([.. folders.Select(f => f.Path.LocalPath)]);
    }

    /// <summary>
    /// The same for the new-instance doorway. Mid-import the command refuses, so a press out
    /// there simply does nothing — which is the right answer rather than a special case.
    /// </summary>
    private void LogScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;

        if (DataContext is InstancesViewModel vm) vm.CloseLiveLogCommand.Execute(null);
    }

    private void NewFlowScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;

        if (DataContext is InstancesViewModel vm) vm.CloseNewFlowCommand.Execute(null);
    }

    /// <summary>
    /// Dismisses the add sheet when the dark area around it is pressed, which is what everyone
    /// expects of a modal.
    ///
    /// Only when the press landed on the scrim itself: a press anywhere inside the card bubbles
    /// up to here as well, and acting on those would close the sheet the moment anyone tried to
    /// use it.
    /// </summary>
    private void AddContentScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;

        if (DataContext is InstancesViewModel vm) vm.CloseAddContentCommand.Execute(null);
    }

    // ---- Card context menu. Each reads its instance off the menu item's Tag rather than the
    // current selection: right-clicking a card must act on that card, not on whichever one was
    // opened last. ----

    private void CardPin_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Instance instance } && DataContext is InstancesViewModel vm)
            vm.TogglePinCommand.Execute(instance);
    }

    private void CardEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Instance instance } && DataContext is InstancesViewModel vm)
            vm.OpenEditForCommand.Execute(instance);
    }

    private void CardSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Instance instance } && DataContext is InstancesViewModel vm)
            vm.OpenSettingsForCommand.Execute(instance);
    }

    private void CardDuplicate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Instance instance } && DataContext is InstancesViewModel vm)
            vm.CloneForCommand.Execute(instance);
    }

    private void CardDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Instance instance } && DataContext is InstancesViewModel vm)
            vm.AskDeleteCommand.Execute(instance);
    }

    private void DeleteGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: InstanceGroup group } && DataContext is InstancesViewModel vm)
            vm.DeleteGroupCommand.Execute(group);
    }

    /// <summary>Export needs a save dialog, and the view model exports whatever is selected.</summary>
    private async void CardExport_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: Instance instance } || DataContext is not InstancesViewModel vm) return;

        vm.Selected = instance;
        await ExportAsync(vm, instance);
    }

    private async void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InstancesViewModel { Selected: { } instance } vm) await ExportAsync(vm, instance);
    }

    private async Task ExportAsync(InstancesViewModel viewModel, Instance instance)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export instance",
            SuggestedFileName = instance.Name,
            DefaultExtension = "zip",
            FileTypeChoices = new List<FilePickerFileType>
            {
                new("Asobu instance") { Patterns = ["*.zip"] },
            },
        });

        if (file is null) return;
        await viewModel.ExportAsync(file.Path.LocalPath);
    }

    private async void ImportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import instance",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Asobu instance") { Patterns = ["*.zip"] },
            },
        });

        if (files.Count == 0) return;
        await vm.ImportAsync(files[0].Path.LocalPath);
    }
}
