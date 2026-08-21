using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Accounts;
using Asobu.Core.Instances;
using Asobu.Core.Mods;
using Asobu.Core.Servers;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>One suggested server, as a row.</summary>
public partial class ServerRow(GameServer server) : ViewModelBase
{
    public GameServer Server { get; } = server;

    public string Name => Server.Name;
    public string Address => Server.Address;
    public string VersionLabel => Server.VersionLabel;

    /// <summary>Says the address is on the clipboard, then goes away again.</summary>
    [ObservableProperty] public partial bool JustCopied { get; set; }
}

/// <summary>
/// A short list of servers worth playing on, with one button that gets you there.
///
/// Suggestions rather than a directory: a list of every server in the world is a website, and one
/// somebody has to scroll is not a suggestion. The five here are written into the launcher, so
/// the page works with no connection and cannot quietly turn into advertising.
/// </summary>
public partial class ServersViewModel : ViewModelBase
{
    private readonly AsobuLauncher _launcher;
    private readonly AccountsViewModel _accounts;
    private readonly AskInstall _askJoin;
    private readonly Func<Instance, string, Task<string?>> _join;

    public ServersViewModel(
        AsobuLauncher launcher,
        AccountsViewModel accounts,
        AskInstall askJoin,
        Func<Instance, string, Task<string?>> join)
    {
        _launcher = launcher;
        _accounts = accounts;
        _askJoin = askJoin;
        _join = join;

        foreach (var server in SuggestedServers.All) Servers.Add(new ServerRow(server));
    }

    public ObservableCollection<ServerRow> Servers { get; } = [];

    /// <summary>
    /// Whether the account signed in can actually get onto any of these.
    ///
    /// Every one of them checks with Mojang that a joining player is who they say they are, which
    /// an offline account has no way of answering. Said on the page rather than found out at the
    /// disconnect screen after a download.
    /// </summary>
    public bool NeedsMicrosoft => _accounts.Active is not { Kind: AccountKind.Microsoft };

    public void OnAccountChanged() => OnPropertyChanged(nameof(NeedsMicrosoft));

    /// <summary>
    /// Flashes "Copied" on a row. The copying itself belongs to the view, which is where every
    /// other clipboard in Asobu is reached from — a view model has no window to ask.
    /// </summary>
    public async Task ShowCopiedAsync(ServerRow row)
    {
        row.JustCopied = true;
        await Task.Delay(1600);
        row.JustCopied = false;
    }

    /// <summary>
    /// Asks which instance to go in with, then launches it pointed at the server.
    ///
    /// The same sheet that chooses an instance for a mod, and greyed the same way: a server that
    /// will not take what an instance runs is worth showing as unavailable rather than hiding, so
    /// the answer to "why isn't my instance here" is on the screen rather than absent from it.
    /// </summary>
    [RelayCommand]
    private void Join(ServerRow? row)
    {
        if (row is null) return;

        _askJoin(
            $"Join {row.Name}",
            instance => _join(instance, row.Server.Address),
            _ => Task.FromResult(SupportFor(row.Server)));
    }

    /// <summary>
    /// Which instances the sheet should leave available, in the shape it already understands.
    ///
    /// Built from the instances themselves rather than from every Minecraft version there has
    /// ever been: the only versions this will ever be asked about are the ones somebody has, and
    /// enumerating the rest to answer questions nobody asks means fetching a manifest to open a
    /// list of five servers.
    /// </summary>
    private ModSupport SupportFor(GameServer server)
    {
        var accepted = _launcher.Instances.LoadAll()
            .Select(instance => instance.MinecraftVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(server.Accepts)
            .Select(version => new ModVersion(
                ModProvider.Modrinth, server.Name, version, [version],

                // No loader named, which the sheet reads as "runs on any" — a server does not
                // care whether the client has Fabric on it.
                [], null, 0, null, "", null, 0, ModChannel.Release))
            .ToList();

        return ModSupport.From(accepted);
    }
}
