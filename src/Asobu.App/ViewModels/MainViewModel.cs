using System;
using System.Net.Http;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Instances;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public MainViewModel()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Asobu/0.1 (+https://asobu.cc)");
        Launcher = new AsobuLauncher(_http);

        AccountsPage = new AccountsViewModel(Launcher);
        InstancesPage = new InstancesViewModel(Launcher, AccountsPage, () => _ = GoNewInstanceAsync());
        NewInstancePage = new VersionPickerViewModel(Launcher, OnInstanceCreated);
        SettingsPage = new SettingsViewModel(Launcher);

        AccountsPage.Reload();
        AccountsPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountsViewModel.Active))
            {
                InstancesPage.RefreshAccountLabel();
                OnPropertyChanged(nameof(AccountLabel));
                OnPropertyChanged(nameof(AccountKindLabel));
            }
        };

        InstancesPage.Reload();
        CurrentPage = InstancesPage;
    }

    public AsobuLauncher Launcher { get; }

    public InstancesViewModel InstancesPage { get; }
    public VersionPickerViewModel NewInstancePage { get; }
    public AccountsViewModel AccountsPage { get; }
    public SettingsViewModel SettingsPage { get; }

    [ObservableProperty] public partial ViewModelBase CurrentPage { get; set; }

    public bool IsInstances => CurrentPage is InstancesViewModel;
    public bool IsNewInstance => CurrentPage is VersionPickerViewModel;
    public bool IsInstancesArea => IsInstances || IsNewInstance;
    public bool IsAccounts => CurrentPage is AccountsViewModel;
    public bool IsSettings => CurrentPage is SettingsViewModel;

    public string AccountLabel => AccountsPage.ActiveLabel;
    public string AccountKindLabel => AccountsPage.ActiveKindLabel;

    partial void OnCurrentPageChanged(ViewModelBase value)
    {
        OnPropertyChanged(nameof(IsInstances));
        OnPropertyChanged(nameof(IsNewInstance));
        OnPropertyChanged(nameof(IsInstancesArea));
        OnPropertyChanged(nameof(IsAccounts));
        OnPropertyChanged(nameof(IsSettings));
    }

    private void OnInstanceCreated(Instance instance)
    {
        InstancesPage.Reload();
        InstancesPage.Selected = instance;
        CurrentPage = InstancesPage;
    }

    [RelayCommand]
    private void GoInstances()
    {
        InstancesPage.Reload();
        CurrentPage = InstancesPage;
    }

    [RelayCommand]
    private async Task GoNewInstanceAsync()
    {
        CurrentPage = NewInstancePage;
        await NewInstancePage.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void GoAccounts()
    {
        AccountsPage.Reload();
        CurrentPage = AccountsPage;
    }

    [RelayCommand]
    private void GoSettings()
    {
        SettingsPage.RefreshJavaOptionsCommand.Execute(null);
        CurrentPage = SettingsPage;
    }
}
