using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Asobu.Core.Launch;

/// <summary>
/// Ties the games to the launcher, so that killing the launcher kills them too.
///
/// Asobu already closes its games politely on the way out, but only when it is given the chance.
/// Ended from Task Manager, or crashed, that code never runs and the game is left behind: still
/// playing, with its multiplayer quietly broken, since the tunnel and the door and the stand-in
/// that vouched for its players all died with the launcher.
///
/// Windows has exactly the right tool. A job object with kill-on-close holds the games, and the
/// handle is released by the operating system when this process ends however it ends. There is
/// nothing to run, so there is nothing that can fail to run.
///
/// Linux has no equivalent that works from the parent's side: the closest, PDEATHSIG, has to be
/// set by the child, and the child here is Java. So on Linux a game outlives a killed launcher,
/// and closing Asobu normally remains the thing that stops it.
/// </summary>
public static class ProcessLeash
{
    private static readonly Lock Gate = new();
    private static nint _job;

    /// <summary>
    /// Puts a game on the leash. Quietly does nothing where that is not possible, since a game
    /// that runs on is better than a game that will not start.
    /// </summary>
    public static bool Hold(Process game)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            lock (Gate)
            {
                if (_job == 0) _job = MakeJob();
                if (_job == 0) return false;

                return AssignProcessToJobObject(_job, game.Handle);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                                   or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static nint MakeJob()
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0) return 0;

        // The whole point: when the last handle to this job closes, everything in it is killed.
        // This process holds the only handle, and Windows closes it whichever way the process
        // ends — including the ways that run no code of ours.
        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnJobClose },
        };

        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var block = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, block, fDeleteOld: false);

            if (!SetInformationJobObject(job, ExtendedLimitInformationClass, block, (uint)size))
            {
                CloseHandle(job);
                return 0;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }

        return job;
    }

    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    // DllImport rather than the newer LibraryImport, which generates code the project would have
    // to allow unsafe blocks to compile. Four calls are not worth loosening that for.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint security, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
