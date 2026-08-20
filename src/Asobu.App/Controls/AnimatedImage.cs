using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Asobu.App.Controls;

/// <summary>
/// An Image that plays a decoded animation when given one, and behaves exactly like an Image
/// when not. Built on Image rather than beside it so stretching, sizing and layout all stay
/// Avalonia's problem — the only thing added here is which frame is showing.
///
/// It plays only while it is on screen: a page of GIFs left behind on another tab has no
/// business waking the dispatcher forty times a second.
/// </summary>
public class AnimatedImage : Image
{
    public static readonly StyledProperty<AnimatedFrames?> FramesProperty =
        AvaloniaProperty.Register<AnimatedImage, AnimatedFrames?>(nameof(Frames));

    public AnimatedFrames? Frames
    {
        get => GetValue(FramesProperty);
        set => SetValue(FramesProperty, value);
    }

    private DispatcherTimer? _timer;
    private int _index;

    /// <summary>Avalonia offers no public "am I attached", so the two overrides below track it.</summary>
    private bool IsAttachedToVisualTree { get; set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        IsAttachedToVisualTree = true;
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        IsAttachedToVisualTree = false;
        Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FramesProperty) Restart();
    }

    private void Restart()
    {
        Stop();

        if (!IsAttachedToVisualTree) return;
        if (Frames is not { Pictures.Count: > 1 } frames) return;

        _index = 0;
        Source = frames.Pictures[0];

        _timer = new DispatcherTimer { Interval = frames.Delays[0] };
        _timer.Tick += Advance;
        _timer.Start();
    }

    private void Advance(object? sender, EventArgs e)
    {
        if (Frames is not { Pictures.Count: > 1 } frames)
        {
            Stop();
            return;
        }

        _index = (_index + 1) % frames.Pictures.Count;
        Source = frames.Pictures[_index];

        // Each frame carries its own delay, so the interval moves with it rather than averaging
        // the whole animation into one speed.
        _timer!.Interval = frames.Delays[_index];
    }

    private void Stop()
    {
        if (_timer is null) return;

        _timer.Stop();
        _timer.Tick -= Advance;
        _timer = null;
    }
}
