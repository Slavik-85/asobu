using Asobu.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Asobu.App.Views;

public partial class ModPageView : UserControl
{
    public ModPageView() => InitializeComponent();

    private async void Title_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock { Text: { Length: > 0 } name }) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try
        {
            await clipboard.SetTextAsync(name);
        }
        catch (System.Exception)
        {
            // Another application holding the clipboard open. The name is on screen to type.
            return;
        }

        CopiedNote.IsVisible = true;
        await System.Threading.Tasks.Task.Delay(1500);
        CopiedNote.IsVisible = false;
    }

    /// <summary>
    /// Escape and the arrow keys, while the full-size viewer is up. A picture filling the screen
    /// with no way out but a small cross in a corner is a trap, and everybody reaches for Escape
    /// before they reach for the mouse.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || DataContext is not ModPageViewModel { IsViewerOpen: true } page) return;

        switch (e.Key)
        {
            case Key.Escape:
                page.CloseViewerCommand.Execute(null);
                break;

            case Key.Left:
                page.PreviousShotCommand.Execute(null);
                break;

            case Key.Right:
                page.NextShotCommand.Execute(null);
                break;

            default:
                return;
        }

        e.Handled = true;
    }
}
