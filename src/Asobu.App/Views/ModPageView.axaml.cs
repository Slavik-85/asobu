using Asobu.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

namespace Asobu.App.Views;

public partial class ModPageView : UserControl
{
    public ModPageView() => InitializeComponent();

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
