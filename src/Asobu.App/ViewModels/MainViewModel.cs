using System;
using System.Net.Http;
using System.Threading.Tasks;
using Asobu.Core.Minecraft;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public MainViewModel()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Asobu/0.1 (+https://asobu.cc)");
        Picker = new VersionPickerViewModel(new MojangMeta(_http));
    }

    public VersionPickerViewModel Picker { get; }

    [ObservableProperty] public partial bool IsAddingInstance { get; set; }

    public bool IsBrowsingInstances => !IsAddingInstance;

    partial void OnIsAddingInstanceChanged(bool value) => OnPropertyChanged(nameof(IsBrowsingInstances));

    [RelayCommand]
    private async Task NewInstanceAsync()
    {
        IsAddingInstance = true;
        await Picker.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void ShowInstances() => IsAddingInstance = false;
}
