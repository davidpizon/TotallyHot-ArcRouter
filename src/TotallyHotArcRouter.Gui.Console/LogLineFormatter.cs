namespace TotallyHot.ArcRouter.Gui.Console;

/// <summary>
/// Formats <see cref="LogLineDto"/>s as the plain text the Console tab displays, so the viewport
/// (<c>ConsoleTab.razor</c>) and the toolbar's "Copy" action produce byte-identical text.
/// </summary>
public static class LogLineFormatter
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Formats one line as "[local timestamp] [LEVEL]  message".</summary>
    public static string Format(LogLineDto line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return
            $"[{line.TimestampUtc.ToLocalTime().ToString(TimestampFormat)}] [{line.Level.PadRight(5)}]  {line.Message}";
    }

    /// <summary>Formats every line, oldest first, one per line, joined with <see cref="Environment.NewLine"/>.</summary>
    public static string FormatAll(IEnumerable<LogLineDto> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return string.Join(separator: Environment.NewLine, values: lines.Select(Format));
    }
}