using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Asobu.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Asobu.App.Views;

public partial class IntroView : UserControl
{
    private AccountsViewModel? _observed;

    public IntroView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Observe((DataContext as IntroViewModel)?.Accounts);
    }

    /// <summary>
    /// Watches the same accounts page the Accounts view watches, for the same reason: the code
    /// has to be typed into a browser in another window, and copying it the moment it appears
    /// is the difference between reading eight characters off a screen and pressing paste.
    ///
    /// The clipboard hangs off the window rather than the view model, so the copying lives here
    /// and the view model only hears that it happened. Both views can hold this subscription
    /// safely: only one of them is ever mounted while a sign-in is running, since the welcome
    /// covers the whole window and the Accounts page is not built until it is navigated to.
    /// </summary>
    private void Observe(AccountsViewModel? accounts)
    {
        if (ReferenceEquals(_observed, accounts)) return;

        if (_observed is not null) _observed.PropertyChanged -= OnAccountsChanged;
        _observed = accounts;
        if (_observed is not null) _observed.PropertyChanged += OnAccountsChanged;
    }

    private void OnAccountsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountsViewModel.DeviceUserCode)
            && sender is AccountsViewModel { DeviceUserCode: { Length: > 0 } code })
        {
            _ = CopyAsync(code, automatic: true);
        }
    }

    private void CopyCode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is IntroViewModel { Accounts.DeviceUserCode: { Length: > 0 } code })
            _ = CopyAsync(code, automatic: false);
    }

    private async Task CopyAsync(string code, bool automatic)
    {
        if (DataContext is not IntroViewModel { Accounts: { } accounts }) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            accounts.NoteCopyFailed();
            return;
        }

        try
        {
            await clipboard.SetTextAsync(code);
            accounts.NoteCopied(automatic);
        }
        catch (Exception)
        {
            // Another application can hold the clipboard open. The code is on screen either way.
            accounts.NoteCopyFailed();
        }
    }
}
