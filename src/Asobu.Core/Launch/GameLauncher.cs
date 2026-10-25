using System.Diagnostics;
using Asobu.Core.Instances;

namespace Asobu.Core.Launch;

/// <summary>Starts Minecraft and keeps its output, so a crash leaves evidence behind.</summary>
public sealed class GameLauncher(AsobuPaths paths)
{
    /// <summary>
    /// Variables Java reads on its own, left behind by some other program's installer. Java applies
    /// _JAVA_OPTIONS after the command line, so a stray "-Xmx512m" there quietly beats the memory
    /// Asobu gave the game, and a bad option in any of them stops the JVM before it starts. Prism
    /// strips the same list, which is a good part of why a pack moved there "just works".
    /// </summary>
    private static readonly string[] JavaVariables =
        ["JAVA_ARGS", "CLASSPATH", "CONFIGPATH", "JAVA_HOME", "JRE_HOME", "_JAVA_OPTIONS", "JAVA_OPTIONS", "JAVA_TOOL_OPTIONS",
         "JDK_JAVA_OPTIONS"];

    /// <summary>Takes the machine's Java variables out of a process about to run Java.</summary>
    public static void CleanJavaEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var name in JavaVariables) startInfo.Environment.Remove(name);
    }

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

        CleanJavaEnvironment(startInfo);

        // After the clean, so a variable somebody set on the instance on purpose still arrives.
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
