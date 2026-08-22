namespace Asobu.Core.Hosting;

/// <summary>
/// What the friends network should be told about the world right now. Null everywhere else in
/// this file means "no world is open", which is a state the host reaches by pressing Escape.
/// </summary>
public sealed record HostedWorld(string Name, int Players, int MaxPlayers, int DoormanPort, string? Version = null);

/// <summary>
/// Watches for the player opening a world to LAN, puts a door in front of it, and keeps the
/// description of it current until they close it again.
///
/// Nothing here asks the player to do anything. The one manual step — Escape, Open to LAN — is
/// unavoidable without a mod, and this exists so that it is the <i>only</i> step: the moment the
/// game starts announcing a world, the door is up and the friends network can be told.
///
/// The beacon is also how hosting ends. A world that closes simply stops announcing, so there is
/// no shutdown to detect, no callback to miss, and a game that crashes outright looks exactly the
/// same as one that was closed politely.
/// </summary>
public sealed class WorldHost : IDisposable
{
    private readonly byte[] _secret;
    private readonly string _hostUsername;
    private readonly Func<CancellationToken, Task<LanWorld?>> _findWorld;
    private readonly Func<int, CancellationToken, Task<WorldStatus?>> _askStatus;

    private WorldDoorman? _doorman;
    private CancellationTokenSource? _doormanStop;
    private int _servingLanPort;
    private int _missed;

    /// <summary>Turns in a row the world has not answered when asked how it is doing.</summary>
    private int _silent;

    /// <summary>
    /// Two missed windows before hosting is called off, not one. Each window already spans several
    /// announcements, so one empty window is a hiccup rather than news — and acting on it would
    /// tear the door down and drop everybody standing behind it.
    /// </summary>
    private const int MissesBeforeClosing = 2;

    /// <summary>Long enough to contain two announcements, which come every 1.5 seconds.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(4);

    public WorldHost(
        byte[] secret,
        string hostUsername,
        Func<CancellationToken, Task<LanWorld?>>? findWorld = null,
        Func<int, CancellationToken, Task<WorldStatus?>>? askStatus = null,
        LanPortWatch? ports = null)
    {
        _secret = secret;
        _hostUsername = hostUsername;

        // The game we started is the one we can hear. The beacon stays as a second answer for a
        // version that stops printing the line, but it is not the one being relied on — see
        // LanPortWatch for why listening for it turned out to be the wrong way round.
        _findWorld = findWorld ?? (async token =>
        {
            if (ports?.Port is { } port)
            {
                // Keeps the loop's cadence. Without it there is nothing to wait on and this spins.
                await Task.Delay(Window, token).ConfigureAwait(false);
                return new LanWorld(port, "");
            }

            return await LanBeacon.FindAsync(Window, token).ConfigureAwait(false);
        });
        _askStatus = askStatus ?? ((port, token) =>
            ServerPing.QueryAsync("127.0.0.1", port, TimeSpan.FromSeconds(3), token));
    }

    /// <summary>The world being hosted, or null when there isn't one.</summary>
    public HostedWorld? Current { get; private set; }

    /// <summary>Fires whenever <see cref="Current"/> becomes something else worth showing.</summary>
    public event Action<HostedWorld?>? Changed;

    /// <summary>
    /// Fires every turn, whether anything changed or not.
    ///
    /// Separate from <see cref="Changed"/> because the two have opposite needs. A screen wants to
    /// hear only about differences; the network wants to be told the world is still there, because
    /// it forgets a world nobody has vouched for lately. Driving the heartbeat off Changed meant a
    /// world with a steady player count was announced once and then quietly forgotten a minute
    /// later — the banner stayed up, and inviting somebody answered "you do not have a world open".
    /// </summary>
    public event Action<HostedWorld?>? Beat;

    /// <summary>Runs until cancelled. The door goes up and comes down underneath it.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
                await OnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            CloseDoor();
            Publish(null);
        }
    }

    /// <summary>One turn of the loop, exposed so a test can drive it a step at a time.</summary>
    internal async Task OnceAsync(CancellationToken cancellationToken)
    {
        var world = await _findWorld(cancellationToken).ConfigureAwait(false);

        if (world is null)
        {
            if (Current is not null && ++_missed >= MissesBeforeClosing)
            {
                CloseDoor();
                Publish(null);
            }

            Beat?.Invoke(Current);
            return;
        }

        _missed = 0;

        // A world reopened on a different port is a different world as far as the door is
        // concerned — the old one leads nowhere now.
        if (_doorman is null || _servingLanPort != world.Port) OpenDoor(world.Port, cancellationToken);

        var status = await _askStatus(world.Port, cancellationToken).ConfigureAwait(false);

        // A port the log told us about, that nothing answers on any more, is a world that was
        // closed without the game exiting — back to the title screen, or off to somebody else's
        // server. The log line cannot say that; only the silence can. Two turns of it, for the
        // same reason the beacon gets two: one missed answer is a hiccup, not an ending.
        if (status is null && Current is not null && ++_silent >= MissesBeforeClosing)
        {
            CloseDoor();
            Publish(null);

            Beat?.Invoke(Current);
            return;
        }

        if (status is not null) _silent = 0;

        // The name comes from whichever source had one: the beacon carries it, the log does not,
        // and the world itself will say when asked.
        var name = world.Name is { Length: > 0 } announced ? announced : status?.Name ?? "A world";

        Publish(new HostedWorld(
            name, status?.Players ?? 0, status?.MaxPlayers ?? 0, _doorman!.Port, status?.Version));

        Beat?.Invoke(Current);
    }

    private void OpenDoor(int lanPort, CancellationToken cancellationToken)
    {
        CloseDoor();

        _doormanStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _doorman = new WorldDoorman(_secret, _hostUsername, lanPort);
        _servingLanPort = lanPort;

        _ = _doorman.RunAsync(_doormanStop.Token);
    }

    private void CloseDoor()
    {
        _doormanStop?.Cancel();
        _doormanStop?.Dispose();
        _doorman?.Dispose();

        _doormanStop = null;
        _doorman = null;
        _servingLanPort = 0;
    }

    /// <summary>
    /// Announces only what changed. The loop runs every few seconds and the player count usually
    /// hasn't moved, so without this the friends network would be told the same thing all evening.
    /// </summary>
    private void Publish(HostedWorld? world)
    {
        if (world == Current) return;

        Current = world;
        Changed?.Invoke(world);
    }

    public void Dispose() => CloseDoor();
}
