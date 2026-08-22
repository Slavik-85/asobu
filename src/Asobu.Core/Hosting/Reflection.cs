using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Asobu.Core.Hosting;

/// <summary>
/// Asks the server what address the internet sees a particular socket at.
///
/// A router rewrites the source port of everything leaving it, and nothing inside can learn the
/// new number by looking. Punching needs it: two machines that cannot accept connections have to
/// dial at each other, and neither can dial an address it does not know.
///
/// The trick is where the question is asked from. Binding to the same local port the tunnel will
/// use means the answer describes that port's own mapping, rather than some other socket's.
/// </summary>
public static class Reflection
{
    private const string Where = "https://api.asobu.cc/v1/reflect";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The public address of <paramref name="localPort"/>, or null when the server cannot be
    /// reached or says something unreadable.
    /// </summary>
    public static async Task<string?> AddressAsync(int localPort, CancellationToken cancellationToken = default)
    {
        // A handler of its own, because the whole point is which socket the request leaves from,
        // and a shared client would reuse a connection made from some other port.
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, token) =>
            {
                var socket = Bind(localPort);
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

        using var http = new HttpClient(handler) { Timeout = Patience };

        try
        {
            var answer = await http.GetStringAsync(Where, cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(answer);
            var address = document.RootElement.TryGetProperty("address", out var value)
                ? value.GetString()
                : null;

            return IPEndPoint.TryParse(address ?? "", out var seen) && seen.Port > 0 ? address : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                   or SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// A socket on a port something else may already be using.
    ///
    /// Punching needs one socket listening and another dialling out from the same port, which the
    /// two platforms spell differently: Windows allows it with ReuseAddress alone, and Linux
    /// refuses until ReusePort is set as well. Both checked on the machines in question rather
    /// than taken from documentation.
    /// </summary>
    internal static Socket Bind(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Share(socket);

        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        return socket;
    }

    /// <summary>Marks a socket as willing to share its port. Safe to call before any bind.</summary>
    internal static void Share(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // SO_REUSEPORT, which .NET does not name. Linux only; on the others it is either the
        // default behaviour or a different number meaning something else entirely.
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)15, true);
        }
        catch (SocketException)
        {
            // An old kernel without it. The bind below will say so more clearly than this could.
        }
    }
}
