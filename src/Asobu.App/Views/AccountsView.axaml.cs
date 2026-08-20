using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Asobu.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Asobu.App.Views;

public partial class AccountsView : UserControl
{
    private AccountsViewModel? _observed;

    public AccountsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe(DataContext as AccountsViewModel);

        // Reposition on resize too: the menu is placed against the button's actual coordinates,
        // and those move whenever the window does.
        PageRoot.SizeChanged += (_, _) => PositionAddMenu();
        AddAccountMenu.SizeChanged += (_, _) => PositionAddMenu();
    }

    /// <summary>
    /// Remembered between opens so the direction can be decided before the menu has been laid
    /// out. Only the very first open falls back to the constant, and two rows is what it is.
    /// </summary>
    private double _menuHeight = 104;

    /// <summary>
    /// Puts the add menu under the button, or over it when there isn't room below, and tells the
    /// styles which way it went. A popup would have done the placement for us, but a popup can't
    /// animate its exit — so this is the price of the fade, and it lives here rather than in the
    /// view model because it is entirely about pixels on screen.
    /// </summary>
    private void PositionAddMenu()
    {
        if (AddAccountButton.TranslatePoint(default, PageRoot) is not { } origin) return;

        const double gap = 10;

        var buttonTop = origin.Y;
        var buttonBottom = origin.Y + AddAccountButton.Bounds.Height;

        if (AddAccountMenu.Bounds.Height > 0) _menuHeight = AddAccountMenu.Bounds.Height;

        var spaceBelow = PageRoot.Bounds.Height - buttonBottom - gap;
        var spaceAbove = buttonTop - gap;

        // Below by preference, above when it doesn't fit — and when neither does, whichever side
        // has more room, so the menu is never pushed off the window entirely.
        var below = spaceBelow >= _menuHeight || spaceBelow >= spaceAbove;

        var centre = origin.X + AddAccountButton.Bounds.Width / 2;
        var widest = Math.Max(8, PageRoot.Bounds.Width - AddAccountMenu.Width - 8);
        var left = Math.Clamp(centre - AddAccountMenu.Width / 2, 8, widest);

        AddAccountMenu.HorizontalAlignment = HorizontalAlignment.Left;
        AddAccountMenu.VerticalAlignment = below ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        AddAccountMenu.Margin = below
            ? new Thickness(left, buttonBottom + gap, 0, 0)
            : new Thickness(left, 0, 0, PageRoot.Bounds.Height - buttonTop + gap);

        // The styles animate away from the button in whichever direction it opened, so the menu
        // always looks like it came out of the button rather than drifting past it.
        AddAccountMenu.Classes.Set("below", below);
    }

    /// <summary>
    /// The clipboard hangs off the window, not the view model, so the copying lives here and the
    /// view model only hears that it happened.
    /// </summary>
    private void Observe(AccountsViewModel? viewModel)
    {
        if (ReferenceEquals(_observed, viewModel)) return;

        if (_observed is not null) _observed.PropertyChanged -= OnViewModelPropertyChanged;
        _observed = viewModel;
        if (_observed is not null) _observed.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A code arriving means a sign-in just started. Copying it straight away saves the one
        // fiddly step in the whole flow — it has to be typed into a page in another window.
        if (e.PropertyName == nameof(AccountsViewModel.DeviceUserCode)
            && sender is AccountsViewModel { DeviceUserCode: { Length: > 0 } code })
        {
            _ = CopyAsync(code, automatic: true);
        }

        // Exact placement once the menu has a size. The direction was already settled on the
        // button press, so this pass only nudges pixels and re-sets the same class.
        if (e.PropertyName == nameof(AccountsViewModel.IsAddMenuOpen))
            Dispatcher.UIThread.Post(PositionAddMenu, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Avalonia raises Click before it runs the Command, so placing the menu here settles the
    /// direction class before the view model turns the menu on. Doing it afterwards meant .open
    /// landed first without the class, firing one animation, and the class arriving a moment
    /// later firing the other — which is exactly the flick.
    /// </summary>
    private void AddAccountButton_Click(object? sender, RoutedEventArgs e) => PositionAddMenu();

    private void CopyCode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AccountsViewModel { DeviceUserCode: { Length: > 0 } code })
            _ = CopyAsync(code, automatic: false);
    }

    private async Task CopyAsync(string code, bool automatic)
    {
        if (DataContext is not AccountsViewModel viewModel) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            viewModel.NoteCopyFailed();
            return;
        }

        try
        {
            await clipboard.SetTextAsync(code);
            viewModel.NoteCopied(automatic);
        }
        catch (System.Exception)
        {
            // Another application can hold the clipboard open. The code is on screen either way.
            viewModel.NoteCopyFailed();
        }
    }
}
