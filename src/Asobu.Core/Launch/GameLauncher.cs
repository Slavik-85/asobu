using System.Diagnostics;
using Asobu.Core.Instances;

namespace Asobu.Core.Launch;

/// <summary>Starts Minecraft and keeps its output, so a crash leaves evidence behind.</summary>
public sealed class GameLauncher(AsobuPaths paths)
{
    public Process Start(LaunchPlan plan, Instance instance, Action<string>? onOutput = null)
    {
        Directory.CreateDirectory(paths.Logs);
        Directory.CreateDirectory(plan.WorkingDirectory);

        var logFile = Path.Combine(paths.Logs, $"{instance.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var log = new StreamWriter(logFile, append: true) { AutoFlush = true };

        var startInfo = new ProcessStartInfo(plan.Executable)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // ArgumentList quotes each argument correctly; never hand-build a command string here.
        foreach (var argument in plan.Arguments) startInfo.ArgumentList.Add(argument);

        foreach (var (key, value) in instance.EnvironmentVariables)
            startInfo.Environment[key] = value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        void Capture(string? line)
        {
            if (line is null) return;
            lock (log) log.WriteLine(line);
            onOutput?.Invoke(line);
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.Exited += (_, _) =>
        {
            lock (log) log.Dispose();
        };

        process.Start();

        // Before anything else it might do: from here on the game dies with this launcher, even
        // when this launcher is not given the chance to close it politely.
        //
        // Said out loud when it fails. A leash that silently did not take looks exactly like one
        // that did, right up until somebody kills the launcher and finds the game still playing.
        if (!ProcessLeash.Hold(process) && OperatingSystem.IsWindows())
            Capture("[asobu] this game is not tied to the launcher; closing Asobu may leave it running");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    public string LogDirectory => paths.Logs;
}
