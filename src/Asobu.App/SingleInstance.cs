using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Asobu.App;

/// <summary>
/// Keeps one Asobu running, and brings it back when someone asks for another.
///
/// Asobu lives in the tray with no window on screen, so starting it again looks to the person
/// like starting it for the first time — and a second copy would fight the first over the same
/// instance folders, accounts and token vault.
///
/// Two mechanisms, because one cannot do both jobs. A mutex answers "is one already running"
/// instantly, which matters: a launch that finds nothing must not pay for asking. A pipe carries
/// the request itself, because the answer to "already running" is not "stop" but "show yourself",
/// and a mutex has no way to say that. Measured before choosing: connecting to a pipe that does
/// not exist costs the whole timeout, ~310ms, on every cold start.
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// Per user rather than per machine: two people signed in to the same computer each get their
    /// own Asobu, and on Windows the unprefixed name is per session as well.
    /// </summary>
    private static readonly string Name = "asobu." + Environment.UserName;

    /// <summary>Held for the life of the process. A field, so nothing collects it.</summary>
    private static Mutex? _claim;

    /// <summary>
    /// True when this is the only Asobu. False means one is already running and should be asked
    /// to come forward instead.
    /// </summary>
    public static bool Claim()
    {
        try
        {
            _claim = new Mutex(initiallyOwned: true, Name, out var first);
            return first;
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or IOException or UnauthorizedAccessException)
        {
            // No way to tell. Starting is the better failure than refusing to start.
            return true;
        }
    }

    /// <summary>
    /// Asks the Asobu that is already running to show itself. Best effort: if it cannot be
    /// reached it has just gone away, and saying so is not useful to anyone.
    /// </summary>
    public static void AskToShow()
    {
        try
        {
            // Windows only lets the foreground process hand foreground rights on. That is this
            // one — it was just launched by a click — and the window that needs to come forward
            // belongs to the other. Without this the tray copy raises a taskbar flash instead.
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(-1);

            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            pipe.Connect(2000);
            pipe.WriteByte(1);
            pipe.Flush();
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Answers later launches for as long as this Asobu lives.</summary>
    public static void Listen(Action onAsked) => _ = Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, maxNumberOfServerInstances: 1);
                await pipe.WaitForConnectionAsync().ConfigureAwait(false);

                if (pipe.ReadByte() >= 0) onAsked();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A caller that hung up part way through. Listen again rather than stop
                // listening, or every later launch would start a second Asobu.
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    });

    /// <summary>ASFW_ANY: any process may take the foreground next, which is the one being asked.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
