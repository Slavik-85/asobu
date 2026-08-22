using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Asobu.Core.Hosting;

/// <summary>
/// A WebSocket seen as a stream of bytes, so the tunnel can treat it exactly like a socket.
/// </summary>
internal sealed class WebSocketStream(WebSocket socket) : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var arrived = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

        // A close frame is the far end hanging up, which for a stream is the end of it.
        return arrived.MessageType == WebSocketMessageType.Close ? 0 : arrived.Count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) socket.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The host's end of the relay: a line held open to the server, and a fresh connection dialled
/// back for each guest that turns up.
///
/// This is what makes hosting work between people who share nothing but the internet. Both ends
/// only ever dial out, which every home router allows, so neither needs to be reachable. It runs
/// over the same 443 as the rest of the API, because an unusual port is the first thing a school
/// or office network blocks and "anywhere" has to mean anywhere.
///
/// Guests still arrive at the doorman rather than at the world, so the pass is checked exactly as
/// it is for somebody on the same LAN. The relay carries bytes and decides nothing.
/// </summary>
public sealed class RelayLink(string url, string bearer, Func<int> doormanPort) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    /// <summary>What a guest quotes to be put through to this host. Null until the server says.</summary>
    public string? Session { get; private set; }

    /// <summary>
    /// Opens the line and keeps it open. Returns once the server has named the session, so the
    /// caller can publish it; the loop behind it runs until this is disposed.
    /// </summary>
    public async Task<bool> OpenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var control = await ConnectAsync($"{url}?role=host", cancellationToken).ConfigureAwait(false);

            var first = await ReadMessageAsync(control, cancellationToken).ConfigureAwait(false);
            if (first is null || Field(first, "session") is not { Length: > 0 } id)
            {
                control.Dispose();
                return false;
            }

            Session = id;
            _ = ListenAsync(control);
            return true;
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or OperationCanceledException or IOException)
        {
            return false;
        }
    }

    /// <summary>Waits for guests. Each one is a note saying "somebody is holding on for you".</summary>
    private async Task ListenAsync(ClientWebSocket control)
    {
        using var line = control;

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var note = await ReadMessageAsync(control, _stop.Token).ConfigureAwait(false);
                if (note is null) return;

                if (Field(note, "open") is { Length: > 0 } ticket) _ = CarryAsync(ticket);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            Session = null;
        }
    }

    /// <summary>
    /// One guest: dial the server back for them, and hold the two ends of them together with our
    /// own door. Nothing here reads what passes through.
    /// </summary>
    private async Task CarryAsync(string ticket)
    {
        try
        {
            using var through = await ConnectAsync($"{url}?role=tunnel&ticket={ticket}", _stop.Token)
                .ConfigureAwait(false);

            await using var relayed = new WebSocketStream(through);

            using var door = new TcpClient();
            await door.ConnectAsync(IPAddress.Loopback, doormanPort(), _stop.Token).ConfigureAwait(false);

            await using var toDoor = door.GetStream();
            await WorldDoorman.RelayAsync(relayed, toDoor, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or SocketException or IOException
                                   or OperationCanceledException or ObjectDisposedException)
        {
            // One guest failing to arrive is one guest, not the end of hosting.
        }
    }

    internal static async Task<ClientWebSocket> ConnectAsync(
        string address, string? bearer, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        if (bearer is { Length: > 0 }) socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);

        await socket.ConnectAsync(new Uri(address), cancellationToken).ConfigureAwait(false);
        return socket;
    }

    private Task<ClientWebSocket> ConnectAsync(string address, CancellationToken cancellationToken) =>
        ConnectAsync(address, bearer, cancellationToken);

    private static async Task<string?> ReadMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        var arrived = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

        return arrived.MessageType == WebSocketMessageType.Close
            ? null
            : Encoding.UTF8.GetString(buffer, 0, arrived.Count);
    }

    private static string? Field(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
