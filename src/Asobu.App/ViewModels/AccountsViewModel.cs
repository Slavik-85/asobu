using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asobu.App.Controls;
using Asobu.Core;
using Asobu.Core.Accounts;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>One tile in the account grid: the head, the name, and how it signs in.</summary>
public partial class AccountCard(Account account) : ViewModelBase
{
    public Account Account { get; } = account;

    public string Username => Account.Username;
    public string KindLabel => Account.Kind == AccountKind.Microsoft ? "Microsoft" : "Offline";

    [ObservableProperty] public partial Bitmap? Face { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; }

    public bool HasFace => Face is not null;

    partial void OnFaceChanged(Bitmap? value) => OnPropertyChanged(nameof(HasFace));
}

public partial class AccountsViewModel(AsobuLauncher launcher) : ViewModelBase
{
    public ObservableCollection<AccountCard> Cards { get; } = [];

    [ObservableProperty] public partial AccountCard? SelectedCard { get; set; }
    [ObservableProperty] public partial string NewUsername { get; set; } = "Player";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool IsSigningIn { get; set; }
    [ObservableProperty] public partial bool ConfirmingRemove { get; set; }

    /// <summary>The offline name sheet, which the "Offline" menu entry opens.</summary>
    [ObservableProperty] public partial bool IsOfflineOpen { get; set; }

    // ---- The add menu. Its own overlay rather than a Flyout: Avalonia tears a popup down the
    // instant it closes, so a flyout can be faded in but never out.

    [ObservableProperty] public partial bool IsAddMenuOpen { get; set; }
    [ObservableProperty] public partial bool IsAddMenuClosing { get; set; }

    // ---- Device code sign-in. The user finishes in a browser, on this machine or another, while
    // the launcher polls; these carry what they need to see meanwhile.

    [ObservableProperty] public partial string? DeviceUserCode { get; set; }
    [ObservableProperty] public partial string? DeviceVerificationUri { get; set; }

    /// <summary>Set by the view once it has actually put the code on the clipboard.</summary>
    [ObservableProperty] public partial string? CopyNotice { get; set; }

    private CancellationTokenSource? _signIn;

    public bool IsAwaitingDeviceCode => DeviceUserCode is { Length: > 0 };

    partial void OnDeviceUserCodeChanged(string? value) => OnPropertyChanged(nameof(IsAwaitingDeviceCode));

    public void NoteCopied(bool automatic) =>
        CopyNotice = automatic ? "Copied for you — just paste it." : "Copied.";

    public void NoteCopyFailed() =>
        CopyNotice = "Couldn't reach the clipboard. Type the code as shown.";

    public bool IsEmpty => Cards.Count == 0;
    public bool HasSelection => SelectedCard is not null;
    public string RemoveLabel => ConfirmingRemove ? "Really?" : "Remove";

    public Account? Active => SelectedCard?.Account;

    public string ActiveLabel => Active?.Username ?? "Not signed in";
    public string ActiveKindLabel => Active is null
        ? "Add an account"
        : Active.Kind == AccountKind.Microsoft ? "Microsoft" : "Offline";

    /// <summary>
    /// Only the registered route needs a client id. Device code carries its own, which is the
    /// whole reason it exists.
    /// </summary>
    public bool NeedsClientId =>
        launcher.Settings.MicrosoftSignIn == AuthMethod.Registered
        && !MicrosoftAuth.IsConfigured(launcher.Settings.MicrosoftClientId);

    public void Reload()
    {
        var previous = Active?.Uuid ?? launcher.Settings.ActiveAccountUuid;

        Cards.Clear();
        foreach (var account in launcher.Accounts.Load()) Cards.Add(new AccountCard(account));

        SelectedCard = Cards.FirstOrDefault(c => c.Account.Uuid == previous) ?? Cards.FirstOrDefault();

        OnPropertyChanged(nameof(IsEmpty));
        foreach (var card in Cards) _ = LoadFaceAsync(card);
    }

    /// <summary>
    /// One avatar fetch per account, cached across reloads. Offline accounts are included: the
    /// service hands back a default head for a UUID it doesn't know, which is exactly right.
    /// </summary>
    private async Task LoadFaceAsync(AccountCard card)
    {
        try
        {
            var face = await SkinFaces.ForAsync(launcher.Http, card.Account.Uuid);
            if (face is null) return;

            await Dispatcher.UIThread.InvokeAsync(() => card.Face = face);
        }
        catch (Exception)
        {
            // Nothing about an avatar is worth taking the page down for.
        }
    }

    partial void OnSelectedCardChanged(AccountCard? value)
    {
        ConfirmingRemove = false;

        foreach (var card in Cards) card.IsActive = ReferenceEquals(card, value);

        OnPropertyChanged(nameof(HasSelection));
        // MainViewModel and the instance page both watch Active, which is computed from this.
        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(ActiveLabel));
        OnPropertyChanged(nameof(ActiveKindLabel));

        if (value is null || launcher.Settings.ActiveAccountUuid == value.Account.Uuid) return;
        launcher.Settings.ActiveAccountUuid = value.Account.Uuid;
        launcher.SaveSettings();
    }

    partial void OnConfirmingRemoveChanged(bool value) => OnPropertyChanged(nameof(RemoveLabel));

    [RelayCommand]
    private void SelectCard(AccountCard? card)
    {
        if (card is not null) SelectedCard = card;
    }

    // ---- Adding ----

    /// <summary>Matches the menu animation in Asobu.axaml; keep the two in step.</summary>
    private const int MenuFadeMilliseconds = 160;

    [RelayCommand]
    private async Task ToggleAddMenuAsync()
    {
        if (IsAddMenuOpen) await CloseAddMenuAsync();
        else IsAddMenuOpen = true;
    }

    /// <summary>
    /// Stays mounted and stays .open for the length of the fade — flipping the flag straight away
    /// would unmount the menu before a single frame of the exit had drawn.
    /// </summary>
    [RelayCommand]
    private async Task CloseAddMenuAsync()
    {
        if (!IsAddMenuOpen || IsAddMenuClosing) return;

        IsAddMenuClosing = true;
        await Task.Delay(MenuFadeMilliseconds);
        IsAddMenuClosing = false;
        IsAddMenuOpen = false;
    }

    [RelayCommand]
    private void StartOffline()
    {
        // Fire and forget: the name sheet opens now and the menu fades out behind its scrim,
        // rather than the user waiting on an animation before anything happens.
        _ = CloseAddMenuAsync();

        Error = null;
        NewUsername = "Player";
        IsOfflineOpen = true;
    }

    [RelayCommand]
    private void CancelOffline() => IsOfflineOpen = false;

    [RelayCommand]
    private void AddOffline()
    {
        Error = null;

        var username = NewUsername.Trim();
        if (username.Length is < 3 or > 16 || !username.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            Error = "Usernames are 3–16 characters: letters, digits and underscores.";
            return;
        }

        if (Cards.Any(c => c.Account.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
        {
            Error = $"{username} is already added.";
            return;
        }

        Add(Account.CreateOffline(username));
        IsOfflineOpen = false;
    }

    [RelayCommand]
    private async Task SignInMicrosoftAsync()
    {
        _ = CloseAddMenuAsync();

        Error = null;
        CopyNotice = null;
        IsSigningIn = true;

        var request = new CancellationTokenSource();
        _signIn = request;

        try
        {
            var account = launcher.Settings.MicrosoftSignIn == AuthMethod.Registered
                ? (await launcher.Microsoft.SignInAsync(request.Token)).Account
                : (await launcher.DeviceCode.SignInAsync(ShowPrompt, request.Token)).Account;

            Add(account);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_signIn, request)) _signIn = null;
            IsSigningIn = false;
            DeviceUserCode = null;
            DeviceVerificationUri = null;
            CopyNotice = null;
        }
    }

    private void Add(Account account)
    {
        // Signing in again as someone already listed replaces them rather than duplicating.
        if (Cards.FirstOrDefault(c => c.Account.Uuid == account.Uuid) is { } existing) Cards.Remove(existing);

        var card = new AccountCard(account);
        Cards.Add(card);

        launcher.Accounts.Save(Cards.Select(c => c.Account));
        SelectedCard = card;

        OnPropertyChanged(nameof(IsEmpty));
        _ = LoadFaceAsync(card);
    }

    /// <summary>
    /// Fires from the polling task, so it hops back to the UI thread before touching bound state.
    /// </summary>
    private void ShowPrompt(DeviceCodePrompt prompt) => Dispatcher.UIThread.Post(() =>
    {
        DeviceUserCode = prompt.UserCode;
        DeviceVerificationUri = prompt.VerificationUri;

        // Opening the page for them saves a step; the code still has to be typed either way.
        try
        {
            AsobuLauncher.OpenUrl(prompt.VerificationUri);
        }
        catch (Exception)
        {
            // No browser, or the shell refused. The link is on screen to open by hand.
        }
    });

    [RelayCommand]
    private void CancelSignIn() => _signIn?.Cancel();

    [RelayCommand]
    private void OpenDevicePage()
    {
        if (DeviceVerificationUri is { Length: > 0 } uri) AsobuLauncher.OpenUrl(uri);
    }

    public void RefreshSignInMode() => OnPropertyChanged(nameof(NeedsClientId));

    // ---- Removing ----

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (SelectedCard is not { } card) return;

        // One click is not enough to drop a signed-in account.
        if (!ConfirmingRemove)
        {
            ConfirmingRemove = true;
            return;
        }

        try { await launcher.SignOutAsync(card.Account); }
        catch (Exception ex) { Error = ex.Message; }

        Cards.Remove(card);
        launcher.Accounts.Save(Cards.Select(c => c.Account));

        ConfirmingRemove = false;
        SelectedCard = Cards.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
