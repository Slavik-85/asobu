using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Input.Platform;
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

    // ---- Carrying a group band to a different place ----
    //
    // Driven from the pointer rather than handed to the system drag loop. That one draws its own
    // cursor, gives no say in what the drag looks like, and holds the message pump for the length
    // of it — so the wheel stops working and the page cannot scroll while a band is in the air.
    // Both of those are the point here, so the pointer is captured and everything is done by hand.

    /// <summary>How far the pointer must travel before a press becomes a drag rather than a click.</summary>
    private const double DragThreshold = 4;

    /// <summary>Distance from the top or bottom edge at which the page starts scrolling itself.</summary>
    private const double AutoScrollMargin = 84;

    /// <summary>Pixels per tick at the very edge, tapering to nothing at the edge of the margin.</summary>
    private const double AutoScrollSpeed = 13;

    /// <summary>
    /// How far the ghost closes on the cursor each frame. Below 1 it trails, which is what makes
    /// it read as carried rather than welded to the pointer.
    /// </summary>
    private const double GhostFollow = 0.3;

    private InstanceGroup? _carrying;
    private Point _pressedAt;
    private bool _carryStarted;

    // Two frames, deliberately. The ghost lives on the Canvas and the bands live inside this
    // control, and the toolbar above them means the two do not share an origin — mixing them up
    // puts the line on the wrong band by exactly the height of the search row.
    private Point _pointerOnPage;   // relative to this control, for finding the band underneath
    private Point _ghostGoing;      // relative to the Canvas, for drawing the ghost

    private Point _ghostAt;
    private double _ghostScale;
    private DispatcherTimer? _carryTimer;

    // Reused rather than rebuilt sixty times a second.
    private readonly RotateTransform _ghostTilt = new();
    private readonly ScaleTransform _ghostSize = new();

    /// <summary>Bumped by every pickup, so a queued fade-out cannot hide a ghost picked up since.</summary>
    private int _carryGeneration;

    private void GroupGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: InstanceGroup band } grip || !band.CanReorder) return;
        if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;

        // Or the press bubbles on to the header button and folds the band away under the pointer.
        e.Handled = true;

        _carrying = band;
        _carryStarted = false;
        _pressedAt = e.GetPosition(this);

        e.Pointer.Capture(grip);
    }

    private void GroupGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_carrying is null || sender is not Control grip) return;
        if (!ReferenceEquals(e.Pointer.Captured, grip)) return;

        var here = e.GetPosition(this);

        // A press that never travels is somebody clicking the header, so nothing is picked up
        // until the pointer has actually gone somewhere.
        if (!_carryStarted)
        {
            var moved = Math.Abs(here.X - _pressedAt.X) + Math.Abs(here.Y - _pressedAt.Y);
            if (moved < DragThreshold) return;

            BeginCarry(e.GetPosition(DragLayer));
        }

        _pointerOnPage = here;
        _ghostGoing = e.GetPosition(DragLayer);

        MarkDropTarget(here);
    }

    private void GroupGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_carrying is null) return;

        var carried = _carrying;
        var landed = _carryStarted ? BandUnder(e.GetPosition(this)) : null;

        EndCarry();
        e.Pointer.Capture(null);

        if (landed is { } drop && DataContext is InstancesViewModel vm)
            vm.MoveGroup(carried.Name, drop.Band.Name, drop.Above);
    }

    /// <summary>Ends the carry wherever it got to — the window losing the pointer counts as let go.</summary>
    private void GroupGrip_CaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndCarry();

    private void BeginCarry(Point at)
    {
        if (_carrying is null) return;

        _carryStarted = true;
        _carryGeneration++;
        _carrying.IsDragging = true;

        DragGhostName.Text = _carrying.Name;
        DragGhost.RenderTransform = new TransformGroup { Children = { _ghostSize, _ghostTilt } };
        DragGhost.Opacity = 1;
        DragGhost.IsVisible = true;

        // Starts under the cursor rather than easing in from wherever it was left last time,
        // and a little small, so it springs up into the hand rather than simply appearing.
        _ghostAt = at;
        _ghostGoing = at;
        _ghostScale = 0.84;
        DrawGhost(0);

        _carryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _carryTimer.Tick -= CarryTick;
        _carryTimer.Tick += CarryTick;
        _carryTimer.Start();
    }

    private void EndCarry()
    {
        _carryTimer?.Stop();

        var wasCarrying = _carryStarted;

        if (_carrying is not null) _carrying.IsDragging = false;
        _carrying = null;
        _carryStarted = false;

        ClearDropLines();

        if (!wasCarrying)
        {
            DragGhost.IsVisible = false;
            return;
        }

        // Faded out rather than snatched away, and unmounted only once the fade has run. The
        // generation is what stops a second pickup, made during that fade, being hidden by the
        // first one's timer.
        var mine = _carryGeneration;
        DragGhost.Opacity = 0;

        DispatcherTimer.RunOnce(
            () => { if (_carryGeneration == mine) DragGhost.IsVisible = false; },
            TimeSpan.FromMilliseconds(170));
    }

    /// <summary>
    /// One frame of the carry: the ghost closes some of the distance to the cursor, and the page
    /// scrolls itself if the pointer is being held near an edge.
    ///
    /// Both belong on a clock rather than on pointer movement. Holding still at the bottom of the
    /// page is exactly when the scrolling is wanted, and that is precisely when no move arrives.
    /// </summary>
    private void CarryTick(object? sender, EventArgs e)
    {
        if (!_carryStarted) return;

        var dx = _ghostGoing.X - _ghostAt.X;
        var dy = _ghostGoing.Y - _ghostAt.Y;

        _ghostAt = new Point(_ghostAt.X + dx * GhostFollow, _ghostAt.Y + dy * GhostFollow);
        _ghostScale += (1 - _ghostScale) * 0.22;

        // Tilt out of how far behind it is running, so it swings when thrown about and settles
        // level when held still. Capped, or a fast flick spins it.
        DrawGhost(Math.Clamp(dx * 0.22, -11, 11));

        AutoScroll();
    }

    private void DrawGhost(double tilt)
    {
        // Held below and right of the cursor, clear of the pointer itself.
        Canvas.SetLeft(DragGhost, _ghostAt.X + 14);
        Canvas.SetTop(DragGhost, _ghostAt.Y + 10);

        _ghostTilt.Angle = tilt;
        _ghostSize.ScaleX = _ghostScale;
        _ghostSize.ScaleY = _ghostScale;
    }

    /// <summary>
    /// Scrolls when the pointer is held near the top or bottom, faster the closer to the edge.
    /// Without it the only bands reachable are the ones that happened to be on screen when the
    /// drag started.
    /// </summary>
    private void AutoScroll()
    {
        if (LibraryScroll is not { } scroller) return;

        var height = scroller.Bounds.Height;
        if (height <= 0) return;

        var y = _ghostGoing.Y;
        var step = 0d;

        if (y < AutoScrollMargin)
            step = -AutoScrollSpeed * (1 - y / AutoScrollMargin);
        else if (y > height - AutoScrollMargin)
            step = AutoScrollSpeed * (1 - (height - y) / AutoScrollMargin);

        if (step == 0) return;

        var reach = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var next = Math.Clamp(scroller.Offset.Y + step, 0, reach);

        if (Math.Abs(next - scroller.Offset.Y) < 0.01) return;

        scroller.Offset = scroller.Offset.WithY(next);

        // The bands have moved under a pointer that has not, so the line has to be worked out
        // again — otherwise it stays on whichever band used to be there. In page coordinates:
        // the band positions are measured in those, and the ghost's are not.
        MarkDropTarget(_pointerOnPage);
    }

    private void MarkDropTarget(Point at)
    {
        ClearDropLines();

        if (BandUnder(at) is not { } drop) return;

        drop.Band.ShowDropAbove = drop.Above;
        drop.Band.ShowDropBelow = !drop.Above;
    }

    /// <summary>
    /// The band beneath a point and which half of it, or nothing where there is no landing to be
    /// had — off the list entirely, over Pinned, or over the band already being carried.
    /// </summary>
    private (InstanceGroup Band, bool Above)? BandUnder(Point at)
    {
        if (BandList?.ItemsPanelRoot is not { } panel) return null;

        foreach (var child in panel.Children)
        {
            if (child is not Control { DataContext: InstanceGroup band } container) continue;
            if (!band.CanReorder || ReferenceEquals(band, _carrying)) continue;

            if (container.TranslatePoint(default, this) is not { } origin) continue;

            var top = origin.Y;
            var bottom = top + container.Bounds.Height;
            if (at.Y < top || at.Y > bottom) continue;

            return (band, at.Y < top + container.Bounds.Height / 2);
        }

        return null;
    }

    private void ClearDropLines()
    {
        if (DataContext is not InstancesViewModel vm) return;

        foreach (var band in vm.InstanceGroups)
        {
            band.ShowDropAbove = false;
            band.ShowDropBelow = false;
        }
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

    /// <summary>Sharing asks which way first; the file half still needs a save dialog.</summary>
    private void CardShare_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: Instance instance } || DataContext is not InstancesViewModel vm) return;

        vm.Selected = instance;
        vm.OpenShare();
    }

    private void ShareButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InstancesViewModel vm) vm.OpenShare();
    }

    /// <summary>Clicking the dimmed area closes the sheet, as it does everywhere else.</summary>
    private void ShareScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;

        if (DataContext is InstancesViewModel vm) vm.CloseShareCommand.Execute(null);
    }

    private async void ShareAsFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel { Selected: { } instance } vm) return;

        // The sheet goes first: a save dialog over a modal is two things asking at once.
        vm.CloseShareCommand.Execute(null);
        await ExportAsync(vm, instance);
    }

    private async void CopyShareCode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel { ShareCodeText: { Length: > 0 } code }) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            await clipboard.SetTextAsync(code);
        }
        catch (Exception)
        {
            // Another application can hold the clipboard. The code is on screen either way.
        }
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
