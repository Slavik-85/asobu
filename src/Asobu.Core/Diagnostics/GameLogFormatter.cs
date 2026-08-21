using System.Globalization;
using System.Text;

namespace Asobu.Core.Diagnostics;

/// <summary>How loud a line is, which is all the colour on screen is saying.</summary>
public enum GameLogLevel
{
    /// <summary>Ordinary progress. The overwhelming majority of any log.</summary>
    Info,

    /// <summary>Something the game worked around and wants noted.</summary>
    Warn,

    /// <summary>Something that failed, and the stack traces underneath it.</summary>
    Error,

    /// <summary>Debug and trace, which mods emit in volume and nobody reads on purpose.</summary>
    Chatter,
}

/// <summary>One finished line, and how loud it was.</summary>
public sealed record GameLogLine(string Text, GameLogLevel Level);

/// <summary>
/// Turns Minecraft's console output into something worth reading.
///
/// The game writes log4j XML to stdout, one event spread over several lines:
///
///   &lt;log4j:Event logger="FabricLoader" timestamp="1787249905593" level="INFO" thread="main"&gt;
///     &lt;log4j:Message&gt;&lt;![CDATA[Loading Minecraft 26.2]]&gt;&lt;/log4j:Message&gt;
///   &lt;/log4j:Event&gt;
///
/// which becomes
///
///   [21:01:07] [main/INFO] [FabricLoader]: Loading Minecraft 26.2
///
/// the shape everyone already recognises from the vanilla console.
///
/// Stateful because an event spans lines and they arrive one at a time from the process. Fed a
/// line, it hands back however many finished lines that produced — usually none until the event
/// closes, then all of it at once.
///
/// Anything that is not part of an event passes straight through: plenty of mods and the JVM
/// itself write plain text to the same stream, and mangling that would lose the very messages
/// people go looking for.
/// </summary>
public sealed class GameLogFormatter
{
    private const string CdataOpen = "<![CDATA[";
    private const string CdataClose = "]]>";

    private string? _level;
    private string? _logger;
    private string? _thread;
    private string? _time;

    /// <summary>Set while a CDATA block is still open across lines.</summary>
    private bool _reading;
    private bool _throwable;

    private readonly List<string> _body = [];
    private readonly StringBuilder _line = new();

    public IEnumerable<GameLogLine> Feed(string raw)
    {
        var line = raw.TrimEnd();

        // Inside a message or stack trace that has not closed yet.
        if (_reading)
        {
            var end = line.IndexOf(CdataClose, StringComparison.Ordinal);

            if (end < 0)
            {
                _body.Add(line);
                yield break;
            }

            if (end > 0) _body.Add(line[..end]);
            _reading = false;

            // A throwable closes the event even without the closing tag on its own line.
            if (_throwable && line.Contains("</log4j:Event>", StringComparison.Ordinal))
                foreach (var done in Flush()) yield return done;

            yield break;
        }

        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("<log4j:Event", StringComparison.Ordinal))
        {
            Start(trimmed);
            yield break;
        }

        if (trimmed.StartsWith("<log4j:Message", StringComparison.Ordinal)
            || trimmed.StartsWith("<log4j:Throwable", StringComparison.Ordinal))
        {
            _throwable = trimmed.StartsWith("<log4j:Throwable", StringComparison.Ordinal);
            ReadBody(trimmed);
            yield break;
        }

        if (trimmed.StartsWith("</log4j:Event>", StringComparison.Ordinal))
        {
            foreach (var done in Flush()) yield return done;
            yield break;
        }

        // Closing tags on their own line, which carry nothing.
        if (trimmed is "</log4j:Message>" or "</log4j:Throwable>") yield break;

        // Not log4j at all. Someone's println, or the JVM's own complaint. Read for a level
        // rather than assumed to be ordinary: a plain-text stack trace is still an error.
        if (_level is null && trimmed.Length > 0) yield return new GameLogLine(line, GuessLevel(trimmed));
    }

    /// <summary>Whatever is still buffered when the process ends mid-event.</summary>
    public IEnumerable<GameLogLine> Drain() => _level is null && _body.Count == 0 ? [] : Flush();

    private void Start(string line)
    {
        _level = Attribute(line, "level");
        _logger = Attribute(line, "logger");
        _thread = Attribute(line, "thread");
        _time = Time(Attribute(line, "timestamp"));
        _body.Clear();
    }

    private void ReadBody(string line)
    {
        var start = line.IndexOf(CdataOpen, StringComparison.Ordinal);
        if (start < 0) return;

        var from = start + CdataOpen.Length;
        var end = line.IndexOf(CdataClose, from, StringComparison.Ordinal);

        if (end >= 0)
        {
            _body.Add(line[from..end]);
            return;
        }

        // Runs on into the following lines.
        _body.Add(line[from..]);
        _reading = true;
    }

    private IEnumerable<GameLogLine> Flush()
    {
        if (_body.Count > 0)
        {
            _line.Clear();

            if (_time is { Length: > 0 }) _line.Append('[').Append(_time).Append("] ");

            if (_thread is { Length: > 0 } || _level is { Length: > 0 })
                _line.Append('[').Append(_thread ?? "main").Append('/').Append(_level ?? "INFO").Append("] ");

            // The logger names the mod, which is most of what a reader is scanning for.
            if (_logger is { Length: > 0 } logger) _line.Append('[').Append(logger).Append(']');
            else if (_line.Length > 0) _line.Length -= 1;

            _line.Append(": ").Append(_body[0]);

            var level = Loudness(_level);

            yield return new GameLogLine(_line.ToString(), level);

            // The rest of a multi-line message, and stack traces, keep their own indentation —
            // repeating the timestamp down the side of a stack trace only makes it harder to
            // read — and the event's level, since a trace under an error is part of the error.
            for (var i = 1; i < _body.Count; i++)
                if (_body[i].Trim().Length > 0)
                    yield return new GameLogLine(_body[i], level);
        }

        _level = _logger = _thread = _time = null;
        _throwable = false;
        _body.Clear();
    }

    /// <summary>log4j's level names, of which only three are worth telling apart on screen.</summary>
    private static GameLogLevel Loudness(string? level) => level?.ToUpperInvariant() switch
    {
        "WARN" or "WARNING" => GameLogLevel.Warn,
        "ERROR" or "FATAL" or "SEVERE" => GameLogLevel.Error,
        "DEBUG" or "TRACE" => GameLogLevel.Chatter,
        _ => GameLogLevel.Info,
    };

    /// <summary>
    /// For output that never went through log4j. Deliberately narrow: it looks for the shapes a
    /// failure actually takes rather than for the word "error" anywhere in a sentence, since a
    /// mod cheerfully logging "no errors found" is not an error.
    /// </summary>
    private static GameLogLevel GuessLevel(string line)
    {
        // Indentation is already trimmed off by the caller, so a frame is recognised by its
        // shape: "at package.Class.method(File.java:42)". The bracket matters — plenty of
        // ordinary sentences start with "at".
        if ((line.StartsWith("at ", StringComparison.Ordinal) && line.Contains('('))
            || line.StartsWith("Caused by:", StringComparison.Ordinal)
            || line.StartsWith("Suppressed:", StringComparison.Ordinal)
            || line.StartsWith("Exception in thread", StringComparison.Ordinal))
            return GameLogLevel.Error;

        if (line.Contains("/ERROR]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("/FATAL]", StringComparison.OrdinalIgnoreCase))
            return GameLogLevel.Error;

        if (line.Contains("/WARN]", StringComparison.OrdinalIgnoreCase)) return GameLogLevel.Warn;

        // The head of a stack trace, which is the line that says what actually went wrong. Its
        // frames underneath were already caught above, so without this the most important line in
        // a crash report was the one rendered as ordinary chatter while everything explaining it
        // was red.
        if (LooksLikeThrowable(line)) return GameLogLevel.Error;

        return GameLogLevel.Info;
    }

    /// <summary>
    /// Whether a line opens with a throwable's own name: "java.lang.NoSuchMethodError: '...'".
    ///
    /// Matched on shape rather than against a list of names, because mods throw their own and
    /// there is no list to keep. A qualified name, no spaces in it, ending in Exception or Error
    /// — which ordinary prose does not manage by accident.
    /// </summary>
    private static bool LooksLikeThrowable(string line)
    {
        var colon = line.IndexOf(':');
        var head = colon < 0 ? line : line[..colon];

        if (head.Length == 0 || head.Contains(' ') || !head.Contains('.')) return false;

        var name = head[(head.LastIndexOf('.') + 1)..];

        return name.EndsWith("Exception", StringComparison.Ordinal)
               || name.EndsWith("Error", StringComparison.Ordinal);
    }

    /// <summary>An attribute out of the opening tag, without paying for an XML parser per line.</summary>
    private static string? Attribute(string line, string name)
    {
        var key = name + "=\"";
        var start = line.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;

        start += key.Length;
        var end = line.IndexOf('"', start);

        return end < 0 ? null : line[start..end];
    }

    /// <summary>log4j counts milliseconds since the epoch; people read clocks.</summary>
    private static string? Time(string? timestamp) =>
        long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis)
            ? DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : null;
}
