using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Accounts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

public partial class AccountsViewModel(AsobuLauncher launcher) : ViewModelBase
{
    public ObservableCollection<Account> Items { get; } = [];

    [ObservableProperty] public partial Account? Active { get; set; }
    [ObservableProperty] public partial string NewUsername { get; set; } = "Player";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool IsSigningIn { get; set; }

    public bool IsEmpty => Items.Count == 0;

    public bool IsMicrosoftConfigured => MicrosoftAuth.IsConfigured(launcher.Settings.MicrosoftClientId);

    public string ActiveLabel => Active?.Username ?? "Not signed in";
    public string ActiveKindLabel => Active is null
        ? "Add an account"
        : Active.Kind == AccountKind.Microsoft ? "Microsoft account" : "Offline account";

    public void Reload()
    {
        Items.Clear();
        foreach (var account in launcher.Accounts.Load()) Items.Add(account);

        Active = Items.FirstOrDefault(a => a.Uuid == launcher.Settings.ActiveAccountUuid)
                 ?? Items.FirstOrDefault();

        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnActiveChanged(Account? value)
    {
        OnPropertyChanged(nameof(ActiveLabel));
        OnPropertyChanged(nameof(ActiveKindLabel));

        if (value is null || launcher.Settings.ActiveAccountUuid == value.Uuid) return;
        launcher.Settings.ActiveAccountUuid = value.Uuid;
        launcher.SaveSettings();
    }

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

        if (Items.Any(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
        {
            Error = $"{username} is already added.";
            return;
        }

        var account = Account.CreateOffline(username);
        Items.Add(account);
        launcher.Accounts.Save(Items);
        Active = account;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task SignInMicrosoftAsync()
    {
        Error = null;
        IsSigningIn = true;
        try
        {
            var (account, _) = await launcher.Microsoft.SignInAsync();

            var existing = Items.FirstOrDefault(a => a.Uuid == account.Uuid);
            if (existing is not null) Items.Remove(existing);

            Items.Add(account);
            launcher.Accounts.Save(Items);
            Active = account;
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(Account? account)
    {
        if (account is null) return;

        if (account.Kind == AccountKind.Microsoft)
        {
            try { await launcher.Microsoft.SignOutAsync(account); }
            catch (Exception ex) { Error = ex.Message; }
        }

        Items.Remove(account);
        launcher.Accounts.Save(Items);

        if (Active == account) Active = Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
