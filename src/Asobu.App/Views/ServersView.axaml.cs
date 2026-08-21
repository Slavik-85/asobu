using Asobu.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Asobu.App.Views;

public partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();

    /// <summary>
    /// The clipboard belongs to the window, so it is reached from here rather than from the view
    /// model — the same way every other copy button in Asobu works.
    /// </summary>
    private async void CopyAddress_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ServerRow row }) return;
        if (DataContext is not ServersViewModel vm) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            await clipboard.SetTextAsync(row.Address);
        }
        catch (System.Exception)
        {
            // Another application holding the clipboard open. Not worth interrupting anybody
            // over: the address is on screen and can be typed.
            return;
        }

        await vm.ShowCopiedAsync(row);
    }
}
