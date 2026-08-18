using System.Globalization;

namespace Asobu.Core;

public static class Format
{
    /// <summary>
    /// Human-readable byte size. Shared by the CLI, download progress and the UI.
    /// Invariant on purpose: the interface is English, so a locale-specific decimal
    /// separator next to English labels just reads as a bug.
    /// </summary>
    public static string Bytes(long value) => value switch
    {
        >= 1L << 30 => Scale(value, 1L << 30, "GB"),
        >= 1L << 20 => Scale(value, 1L << 20, "MB"),
        >= 1L << 10 => Scale(value, 1L << 10, "KB"),
        _ => $"{value} B",
    };

    private static string Scale(long value, long unit, string suffix) =>
        (value / (double)unit).ToString("0.0", CultureInfo.InvariantCulture) + " " + suffix;
}
