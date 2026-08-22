using System.Net;
using System.Net.Sockets;
using System.Text.Json;

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
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How the host says "the server will carry this one".</summary>
    public const string RelayPrefix = "relay:";

    /// <summary>How the host says "dial me here, but tell me first so I can open the way".</summary>
    public const string PunchPrefix = "punch:";

    /// <summary>Where a guest says it is about to dial, so the host can fire back at it.</summary>
    private const string PunchUrl = "https://api.asobu.cc/v1/relay/punch";

    /// <summary>Same 443 as the rest of the API, so no network has a reason to object to it.</summary>
    public const string RelayUrl = "wss://api.asobu.cc/v1/relay";

    private readonly WorldGuest _guest;
    private readonly CancellationTokenSource _stop = new();

    private WorldJoin(WorldGuest guest, string door)
    {
        _guest = guest;
        Door = door;

        _ = guest.RunAsync(_stop.Token);
    }

    /// <summary>Which of the host's addresses turned out to be the one that works.</summary>
    public string Door { get; }

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
        // The relay session is how a punching guest reaches the host to say it is coming, so it
        // has to be found before the routes that need it are built.
        var session = addresses
            .FirstOrDefault(a => a.StartsWith(RelayPrefix, StringComparison.Ordinal))?[RelayPrefix.Length..];

        var routes = addresses.Select(address => Route.Read(address, session)).OfType<Route>().ToList();

        if (routes.Count == 0)
            throw new WorldJoinException("They haven't said where to find them yet. Try again in a moment.");

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var probes = routes.Select(route => ProbeAsync(route, pass, race.Token)).ToList();
        Route? door = null;
        var refused = false;

        while (probes.Count > 0)
        {
            var finished = await Task.WhenAny(probes).ConfigureAwait(false);
            probes.Remove(finished);

            var (route, outcome) = await finished.ConfigureAwait(false);
            if (outcome == Outcome.Reached)
            {
                door = route;
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
                : "Couldn't reach them at all. They may have closed the world, or gone offline.");

        return new WorldJoin(new WorldGuest(door.Open, pass), door.Label);
    }

    /// <summary>
    /// One way of getting hold of the host: straight to their machine, or through the relay when
    /// nothing can reach it. Both end up as a stream, so everything after this stops caring which.
    /// </summary>
    private sealed record Route(string Label, Func<CancellationToken, Task<Stream>> Open)
    {
        /// <summary>
        /// Reads one of the addresses the host published. "relay:abc" is the server offering to
        /// carry it; anything else is an address to try directly. Null for anything unreadable,
        /// including a bare IP with no port, which TryParse otherwise accepts as port zero.
        /// </summary>
        public static Route? Read(string address, string? relaySession)
        {
            // Somewhere to dial that will not answer until the host has been told to expect it.
            if (address.StartsWith(PunchPrefix, StringComparison.Ordinal))
            {
                var where = address[PunchPrefix.Length..];
                if (relaySession is not { Length: > 0 } session) return null;
                if (!IPEndPoint.TryParse(where, out var peer) || peer.Port <= 0) return null;

                return new Route(address, token => PunchThroughAsync(peer, session, token));
            }

            if (address.StartsWith(RelayPrefix, StringComparison.Ordinal))
            {
                var session = address[RelayPrefix.Length..];
                if (session.Length == 0) return null;

                return new Route(address, async token =>
                {
                    var socket = await RelayLink
                        .ConnectAsync($"{RelayUrl}?role=guest&session={session}", null, token)
                        .ConfigureAwait(false);

                    return new WebSocketStream(socket);
                });
            }

            if (!IPEndPoint.TryParse(address, out var endpoint) || endpoint.Port <= 0) return null;

            return new Route(address, async token =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(endpoint, token).ConfigureAwait(false);

                return client.GetStream();
            });
        }
    }

    /// <summary>
    /// Dials the host directly, having first asked them to dial back.
    ///
    /// The order is the whole trick. A router drops an incoming connection nobody asked for, so
    /// the host is told where this is coming from and fires at it; their router then treats what
    /// arrives as an answer rather than a stranger. Both sides do it at once, and one of the two
    /// gets through.
    ///
    /// Everything happens from one local port: the request that tells the host where to fire
    /// leaves from it, so what the server sees is this port's own mapping, and the connection
    /// afterwards leaves from it too, so what arrives at the host matches what they were told.
    /// </summary>
    private static async Task<Stream> PunchThroughAsync(
        IPEndPoint host, string session, CancellationToken cancellationToken)
    {
        var mine = FreePort();

        await AnnounceAsync(mine, session, cancellationToken).ConfigureAwait(false);

        // Several tries, because the first may leave before the host's own has, and a router
        // that has not yet seen anything go out will still be dropping what comes back.
        for (var attempt = 0; ; attempt++)
        {
            var socket = Reflection.Bind(mine);
            try
            {
                using var brief = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                brief.CancelAfter(TimeSpan.FromMilliseconds(700));

                await socket.ConnectAsync(host, brief.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception) when (attempt < PunchAttempts)
            {
                socket.Dispose();
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Tells the host to expect us, from the socket they should expect. The server reads the
    /// address off the connection rather than being told it, so this cannot be used to point
    /// somebody else's machine at a third party.
    /// </summary>
    private static async Task AnnounceAsync(int fromPort, string session, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, token) =>
            {
                var socket = Reflection.Bind(fromPort);
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        using var body = new StringContent(
            JsonSerializer.Serialize(new { session }), System.Text.Encoding.UTF8, "application/json");

        using var _ = await http.PostAsync(PunchUrl, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A port nothing is using, released again so the punch can claim it properly.</summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    private const int PunchAttempts = 6;

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
    private static async Task<(Route Route, Outcome Outcome)> ProbeAsync(
        Route route, string pass, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            await using var stream = await route.Open(timeout.Token).ConfigureAwait(false);
            await Handshake.WriteLineAsync(stream, $"{Handshake.Greeting} {pass}", timeout.Token).ConfigureAwait(false);

            var answer = await Handshake.ReadLineAsync(stream, timeout.Token).ConfigureAwait(false);
            return (route, answer == Handshake.Accepted ? Outcome.Reached : Outcome.Refused);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException
                                   or ObjectDisposedException or System.Net.WebSockets.WebSocketException
                                   or HttpRequestException)
        {
            return (route, Outcome.Unreachable);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _guest.Dispose();
        _stop.Dispose();
    }
}
