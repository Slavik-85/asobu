using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Asobu.Core;
using Asobu.Core.Online;

namespace Asobu.Core.Tests;

/// <summary>
/// The machine digest that caps how many offline accounts one computer may put on the network.
///
/// Two properties matter and they pull against each other: it has to be the same answer every
/// time on one machine, or the ceiling is no ceiling at all — and it must never be the machine's
/// own identifier, because that is a durable fingerprint the server has no use for.
/// </summary>
public class MachineIdTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-machine-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AsobuPaths Paths(string who) => new(Path.Combine(_root, who));

    [Fact]
    public void Is_a_sha256_digest()
    {
        var id = MachineId.ForNetwork(Paths("a"));

        Assert.Equal(64, id.Length);
        Assert.True(id.All(Uri.IsHexDigit), $"not hex: {id}");
    }

    [Fact]
    public void Answers_the_same_thing_every_time()
    {
        Assert.Equal(MachineId.ForNetwork(Paths("a")), MachineId.ForNetwork(Paths("a")));
    }

    /// <summary>
    /// On Windows the underlying value is the one Windows itself keeps, so the answer must not
    /// depend on where Asobu's own files happen to live — a second install on one computer is
    /// still that one computer, and must not get five more accounts for the trouble.
    /// </summary>
    [Fact]
    public void Does_not_change_with_the_data_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (MachineGuidByAnotherRoute() is null) return;

        Assert.Equal(MachineId.ForNetwork(Paths("one")), MachineId.ForNetwork(Paths("two")));
    }

    /// <summary>
    /// Reads the value it is meant to read, rather than quietly falling through to the
    /// per-install fallback.
    ///
    /// Worth pinning down, because the registry read is a hand-written P/Invoke and a wrong flag
    /// there fails by returning nothing rather than by throwing — which looks exactly like
    /// working, right up until two installs on one machine disagree about which machine they are
    /// on. The value is fetched here through reg.exe instead, so this compares the P/Invoke
    /// against something rather than against a copy of itself.
    /// </summary>
    [Fact]
    public void Comes_from_the_machine_guid_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (MachineGuidByAnotherRoute() is not { } guid) return;

        var expected = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("asobu-machine " + guid)));

        Assert.Equal(expected, MachineId.ForNetwork(Paths("a")));
    }

    /// <summary>The raw value must not be recoverable from what gets sent, nor appear inside it.</summary>
    [Fact]
    public void Never_carries_the_underlying_value()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (MachineGuidByAnotherRoute() is not { } guid) return;

        var id = MachineId.ForNetwork(Paths("a"));

        Assert.DoesNotContain(guid, id, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(guid.Replace("-", ""), id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same registry value, fetched without going anywhere near the code under test.</summary>
    private static string? MachineGuidByAnotherRoute()
    {
        try
        {
            using var reg = Process.Start(new ProcessStartInfo("reg")
            {
                Arguments = @"query HKLM\SOFTWARE\Microsoft\Cryptography /v MachineGuid /reg:64",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (reg is null) return null;

            var output = reg.StandardOutput.ReadToEnd();
            reg.WaitForExit(10_000);

            var found = Regex.Match(output, @"MachineGuid\s+REG_SZ\s+(\S+)");
            return found.Success ? found.Groups[1].Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
