using System.Net;
using System.Net.Sockets;

namespace Asobu.Core.Hosting;

/// <summary>A refusal fit to show a player as-is.</summary>
public sealed class WorldJoinException(string message) : Exception(message);

/// <summary>
/// A friend's world, reachable at an address on this machine.
///
/// The host offers several addresses — their LAN, whatever VPN they are on, and the one the API
/// saw them arrive from — and only one of them will work from here. Which one is not something
/// either side can work out by reasoning about it: the same friend is on your network today and
/// across the country tomorrow. So all of them are tried at once and the first to answer wins,
/// which also means the cheapest route is usually the one taken, because it answers soonest.
///
/// Tried at once rather than in turn on purpose. A friend on the internet advertises their LAN
/// address first, and connecting to somebody else's 192.168 address does not fail quickly — it
/// hangs until it times out. Working through the list in order would put that wait in front of
/// every join that leaves the house.
/// </summary>
public sealed class WorldJoin : IDisposable
{
    /// <summary>
    /// Long enough for a distant host on a bad line, short enough that a dead address does not
    /// hold up a join that was going to work anyway.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private readonly WorldGuest _guest;
    private readonly CancellationTokenSource _stop = new();

    private WorldJoin(WorldGuest guest, IPEndPoint door)
    {
        _guest = guest;
        Door = door;

        _ = guest.RunAsync(_stop.Token);
    }

    /// <summary>Which of the host's addresses turned out to be the one that works.</summary>
    public IPEndPoint Door { get; }

    /// <summary>Give the game this, and it joins the friend's world.</summary>
    public string Address => $"127.0.0.1:{_guest.Port}";

    /// <summary>
    /// Finds a way through to the host and opens a door on this machine for the game to use.
    ///
    /// The cancellation token covers the search only. What it returns outlives it and stops when
    /// disposed — a token that ended with the click that started the join would take the tunnel
    /// down while the player was still in the world.
    /// </summary>
    public static async Task<WorldJoin> ReachAsync(
        IEnumerable<string> addresses, string pass, CancellationToken cancellationToken = default)
    {
        // The port is checked separately because TryParse is happy to read a bare "1.2.3.4" and
        // hand back port zero, which is not an address anybody is listening on.
        var candidates = addresses
            .Select(text => IPEndPoint.TryParse(text, out var endpoint) && endpoint.Port > 0 ? endpoint : null)
            .OfType<IPEndPoint>()
            .ToList();

        if (candidates.Count == 0)
            throw new WorldJoinException("They haven't said where to find them yet. Try again in a moment.");

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var probes = candidates.Select(endpoint => ProbeAsync(endpoint, pass, race.Token)).ToList();
        IPEndPoint? door = null;
        var refused = false;

        while (probes.Count > 0)
        {
            var finished = await Task.WhenAny(probes).ConfigureAwait(false);
            probes.Remove(finished);

            var (endpoint, outcome) = await finished.ConfigureAwait(false);
            if (outcome == Outcome.Reached)
            {
                door = endpoint;
                break;
            }

            // Worth remembering rather than acting on: another address may still let us in, and
            // only when none of them do does being turned away become the thing to report.
            if (outcome == Outcome.Refused) refused = true;
        }

        // Whatever is still knocking is knocking on a door we no longer need.
        race.Cancel();

        if (door is null)
            throw new WorldJoinException(refused
                ? "They haven't invited you, or the invite has run out. Ask them to invite you again."
                : "Couldn't reach their machine. They may have closed the world, or their network is in the way.");

        return new WorldJoin(new WorldGuest(door, pass), door);
    }

    private enum Outcome
    {
        /// <summary>Nothing answered: wrong network, closed world, firewall.</summary>
        Unreachable,

        /// <summary>The door is there and said no. A different address will say no too.</summary>
        Refused,

        Reached,
    }

    /// <summary>
    /// Knocks, and reports what happened. Never throws: a dead address is the expected answer for
    /// most of the list, and the caller is asking all of them at once.
    ///
    /// Knocking properly rather than merely opening a socket — the pass goes in and the answer
    /// comes back — because a bare connection proves only that something is listening on that
    /// port, which on somebody's home network is not the same as it being their world.
    /// </summary>
    private static async Task<(IPEndPoint Endpoint, Outcome Outcome)> ProbeAsync(
        IPEndPoint endpoint, string pass, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var client = new TcpClient();
            await client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);

            await using var stream = client.GetStream();
            await Handshake.WriteLineAsync(stream, $"{Handshake.Greeting} {pass}", timeout.Token).ConfigureAwait(false);

            var answer = await Handshake.ReadLineAsync(stream, timeout.Token).ConfigureAwait(false);
            return (endpoint, answer == Handshake.Accepted ? Outcome.Reached : Outcome.Refused);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            return (endpoint, Outcome.Unreachable);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _guest.Dispose();
        _stop.Dispose();
    }
}
