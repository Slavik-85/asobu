using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Minecraft;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

public partial class InstancesViewModel(AsobuLauncher launcher, AccountsViewModel accounts, Action requestNewInstance) : ViewModelBase
{
    public ObservableCollection<Instance> Items { get; } = [];

    [ObservableProperty] public partial Instance? Selected { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool ConfirmingDelete { get; set; }

    public bool IsEmpty => Items.Count == 0;
    public bool HasSelection => Selected is not null;
    public bool CanPlay => Selected is not null && !IsBusy && !IsRunning;
    public string PlayLabel => IsRunning ? "Running" : IsBusy ? "Working…" : "Play";
    public string DeleteLabel => ConfirmingDelete ? "Really delete?" : "Delete";
    public string AccountLabel => accounts.Active is { } a ? $"as {a.Username}" : "no account selected";

    public void Reload()
    {
        var previous = Selected?.Id;

        Items.Clear();
        foreach (var instance in launcher.Instances.LoadAll()) Items.Add(instance);

        Selected = Items.FirstOrDefault(i => i.Id == previous) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedChanged(Instance? value)
    {
        ConfirmingDelete = false;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanPlay));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayLabel));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayLabel));
    }

    partial void OnConfirmingDeleteChanged(bool value) => OnPropertyChanged(nameof(DeleteLabel));

    public void RefreshAccountLabel() => OnPropertyChanged(nameof(AccountLabel));

    [RelayCommand]
    private void NewInstance() => requestNewInstance();

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (Selected is not { } instance) return;

        Error = null;

        if (accounts.Active is not { } account)
        {
            Error = "Add an account before playing.";
            return;
        }

        IsBusy = true;
        Progress = 0;
        Status = "Preparing";

        try
        {
            var session = await launcher.ResolveSessionAsync(account);

            var reporter = new Progress<InstallProgress>(p =>
            {
                Status = p.Stage;
                Progress = p.Fraction;
            });

            var startedAt = DateTimeOffset.UtcNow;
            var process = await launcher.LaunchAsync(instance, session, reporter);

            IsRunning = true;
            Status = "Minecraft is running";
            Progress = 1;

            _ = TrackAsync(process, instance, startedAt);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Waits for the game off the UI thread, then records playtime.</summary>
    private async Task TrackAsync(Process process, Instance instance, DateTimeOffset startedAt)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (SystemException)
        {
        }

        var exitCode = process.ExitCode;
        var played = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds;

        instance.PlaytimeSeconds += played;
        launcher.Instances.Save(instance);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsRunning = false;
            Status = "";
            // A non-zero exit after the game was up means a crash, not a normal quit.
            if (exitCode != 0)
                Error = $"Minecraft exited with code {exitCode}. The log is in {launcher.Paths.Logs}.";
            Reload();
        });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Selected is { } instance)
            AsobuLauncher.OpenFolder(launcher.Paths.InstanceGameDir(instance.Id));
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } instance) return;

        // Instances hold worlds. One click must never be enough.
        if (!ConfirmingDelete)
        {
            ConfirmingDelete = true;
            return;
        }

        launcher.Instances.Delete(instance);
        ConfirmingDelete = false;
        Reload();
    }
}
