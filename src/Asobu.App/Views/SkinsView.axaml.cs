using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Asobu.App.ViewModels;

namespace Asobu.App.Views;

public partial class SkinsView : UserControl
{
    /// <summary>Where the drag started, so the figure turns by how far it moved.</summary>
    private Point? _turningFrom;

    private bool _drawing;

    /// <summary>True while the left button is down on the figure with the editor open.</summary>
    private bool _paintingFigure;

    public SkinsView()
    {
        InitializeComponent();

        var stage = this.FindControl<Border>("Stage")!;
        stage.PointerPressed += StartTurning;
        stage.PointerMoved += KeepTurning;
        stage.PointerReleased += StopTurning;
        stage.PointerCaptureLost += (_, _) =>
        {
            _turningFrom = null;
            _paintingFigure = false;
        };

        var canvas = this.FindControl<Image>("Canvas")!;
        canvas.PointerPressed += StartDrawing;
        canvas.PointerMoved += KeepDrawing;
        canvas.PointerReleased += (_, _) => _drawing = false;
        canvas.PointerCaptureLost += (_, _) => _drawing = false;

        // The shelf grows as it is scrolled, so the gallery never ends in a wall.
        this.FindControl<ScrollViewer>("GalleryScroll")!.ScrollChanged += (sender, args) =>
        {
            if (sender is not ScrollViewer scroll || Model is not { } model) return;

            // Within a card's height of the bottom, which is early enough that the next page is
            // usually there before the scroll reaches where it would have stopped.
            if (scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 180)
                _ = model.LoadMoreGalleryAsync();
        };

        this.FindControl<Button>("UploadButton")!.Click += (_, _) => _ = UploadAsync();
        this.FindControl<Button>("ExportButton")!.Click += (_, _) => _ = ExportAsync();
        this.FindControl<Button>("ImportDrawButton")!.Click += (_, _) => _ = ImportForEditingAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private SkinsViewModel? Model => DataContext as SkinsViewModel;

    // ---- Turning the figure ----

    /// <summary>
    /// Left draws on the figure, right turns it.
    ///
    /// Two jobs on one surface, so they need two buttons. Left is the drawing one because that is
    /// the button a pencil is held in everywhere else; outside the drawing tab there is nothing
    /// to draw, so left turns it as well and the distinction never comes up.
    /// </summary>
    private void StartTurning(object? sender, PointerPressedEventArgs e)
    {
        if (Model is not { } model || sender is not Border stage) return;

        var point = e.GetCurrentPoint(stage);

        if (model.IsDraw && point.Properties.IsLeftButtonPressed)
        {
            model.BeginStroke();
            _paintingFigure = true;

            e.Pointer.Capture(stage);
            PaintOnFigure(model, stage, point.Position);
            return;
        }

        _turningFrom = e.GetPosition(this);
        e.Pointer.Capture(stage);
    }

    private void KeepTurning(object? sender, PointerEventArgs e)
    {
        if (Model is not { } model || sender is not Border stage) return;

        if (_paintingFigure)
        {
            PaintOnFigure(model, stage, e.GetPosition(stage));
            return;
        }

        if (_turningFrom is not { } from) return;

        var now = e.GetPosition(this);
        model.Drag(now.X - from.X, now.Y - from.Y);

        _turningFrom = now;
    }

    /// <summary>
    /// Where on the rendered figure a point on screen lands.
    ///
    /// The figure is a fixed-size picture stretched uniformly into whatever room the card has, so
    /// getting back to its own pixels means undoing exactly that fit — the same arithmetic the
    /// sheet needs, against a picture that is taller than it is wide.
    /// </summary>
    private static void PaintOnFigure(SkinsViewModel model, Border stage, Point point)
    {
        const double figureWidth = 260, figureHeight = 340;

        // The stage has a 10px margin round the picture inside it.
        var w = stage.Bounds.Width - 20;
        var h = stage.Bounds.Height - 20;
        if (w <= 0 || h <= 0) return;

        var scale = Math.Min(w / figureWidth, h / figureHeight);
        var left = 10 + (w - figureWidth * scale) / 2;
        var top = 10 + (h - figureHeight * scale) / 2;

        model.PaintOnFigure((point.X - left) / scale, (point.Y - top) / scale);
    }

    private void StopTurning(object? sender, PointerReleasedEventArgs e)
    {
        _turningFrom = null;
        _paintingFigure = false;
        e.Pointer.Capture(null);
    }

    // ---- Drawing ----

    private void StartDrawing(object? sender, PointerPressedEventArgs e)
    {
        if (Model is not { } model || sender is not Image canvas) return;

        // One undo step per stroke, taken before the first pixel of it changes.
        model.BeginStroke();
        _drawing = true;

        e.Pointer.Capture(canvas);
        PaintAt(model, canvas, e.GetPosition(canvas));
    }

    private void KeepDrawing(object? sender, PointerEventArgs e)
    {
        if (!_drawing || Model is not { } model || sender is not Image canvas) return;

        PaintAt(model, canvas, e.GetPosition(canvas));
    }

    /// <summary>
    /// Turns a point on screen into a pixel of the sheet.
    ///
    /// The image is stretched uniformly inside whatever room it was given, so the sheet is the
    /// largest square that fits, centred. Working that out here rather than asking the control
    /// is the only way to be exact — and being one pixel out is the difference between drawing
    /// on an arm and drawing on the air beside it.
    /// </summary>
    private static void PaintAt(SkinsViewModel model, Image canvas, Point point)
    {
        var side = Math.Min(canvas.Bounds.Width, canvas.Bounds.Height);
        if (side <= 0) return;

        var left = (canvas.Bounds.Width - side) / 2;
        var top = (canvas.Bounds.Height - side) / 2;

        var x = (int)Math.Floor((point.X - left) / side * 64);
        var y = (int)Math.Floor((point.Y - top) / side * 64);

        model.Paint(x, y);
    }

    // ---- Files ----

    private async Task UploadAsync()
    {
        if (Model is not { } model || TopLevel.GetTopLevel(this) is not { } top) return;

        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a skin",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Minecraft skin") { Patterns = ["*.png"] }],
        });

        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) model.ImportFromFile(path);
    }

    /// <summary>A skin from disk, straight onto the drawing board.</summary>
    private async Task ImportForEditingAsync()
    {
        if (Model is not { } model || TopLevel.GetTopLevel(this) is not { } top) return;

        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a skin to edit",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Minecraft skin") { Patterns = ["*.png"] }],
        });

        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) model.ImportForEditing(path);
    }

    private async Task ExportAsync()
    {
        if (Model is not { } model || TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save skin",
            SuggestedFileName = "skin.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });

        if (file?.TryGetLocalPath() is { } path) model.ExportTo(path);
    }
}
