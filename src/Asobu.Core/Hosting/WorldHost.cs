namespace Asobu.Core.Hosting;

/// <summary>
/// What the friends network should be told about the world right now. Null everywhere else in
/// this file means "no world is open", which is a state the host reaches by pressing Escape.
/// </summary>
public sealed record HostedWorld(string Name, int Players, int MaxPlayers, int DoormanPort);

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
        Func<int, CancellationToken, Task<WorldStatus?>>? askStatus = null)
    {
        _secret = secret;
        _hostUsername = hostUsername;
        _findWorld = findWorld ?? (token => LanBeacon.FindAsync(Window, token));
        _askStatus = askStatus ?? ((port, token) =>
            ServerPing.QueryAsync("127.0.0.1", port, TimeSpan.FromSeconds(3), token));
    }

    /// <summary>The world being hosted, or null when there isn't one.</summary>
    public HostedWorld? Current { get; private set; }

    /// <summary>Fires whenever <see cref="Current"/> becomes something else worth publishing.</summary>
    public event Action<HostedWorld?>? Changed;

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

            return;
        }

        _missed = 0;

        // A world reopened on a different port is a different world as far as the door is
        // concerned — the old one leads nowhere now.
        if (_doorman is null || _servingLanPort != world.Port) OpenDoor(world.Port, cancellationToken);

        var status = await _askStatus(world.Port, cancellationToken).ConfigureAwait(false);

        Publish(new HostedWorld(world.Name, status?.Players ?? 0, status?.MaxPlayers ?? 0, _doorman!.Port));
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
