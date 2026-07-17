using System.Text;
using System.Text.RegularExpressions;

namespace AiUsageTray.Infrastructure;

/// <summary>
/// Strips ANSI/VT terminal control sequences from captured pseudoconsole output so downstream code
/// works with plain text. Kept separate from any parser so both the parser and safe-logging paths
/// can reuse it. Intentionally conservative: it removes control sequences and lone control chars but
/// leaves ordinary (including non-ASCII) text alone.
/// </summary>
public static partial class AnsiText
{
    // Cursor-movement and erase sequences (ESC [ ... H/f/A/B/J/K). An interactive panel repaints by
    // repositioning the cursor rather than emitting newlines, so for line-oriented parsing these
    // must become line breaks - otherwise a repainted "51% used" merges onto the "Esc to cancel"
    // line and can't be read.
    [GeneratedRegex(@"\x1B\[[0-9;]*[HfABJK]")]
    private static partial Regex LayoutRegex();

    // Remaining CSI sequences: SGR colors, private modes (?...h/l), etc. - purely decorative.
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex CsiRegex();

    // OSC sequences: ESC ] ... terminated by BEL (\x07) or ST (ESC \).
    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)")]
    private static partial Regex OscRegex();

    // Any remaining two-char escape (ESC followed by a single byte), plus a stray trailing ESC.
    [GeneratedRegex(@"\x1B[@-Z\\-_]?")]
    private static partial Regex OtherEscapeRegex();

    /// <summary>Removes every control sequence, collapsing layout moves away entirely. Use for logging.</summary>
    public static string Strip(string input) => StripInternal(input, layoutToNewline: false);

    /// <summary>
    /// Produces plain text suitable for line-oriented parsing: cursor moves and line erases become
    /// newlines, CRLF/CR normalize to LF, and all other control sequences are removed.
    /// </summary>
    public static string StripAndNormalizeNewlines(string input) => StripInternal(input, layoutToNewline: true);

    private static string StripInternal(string input, bool layoutToNewline)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var text = OscRegex().Replace(input, string.Empty);
        text = LayoutRegex().Replace(text, layoutToNewline ? "\n" : string.Empty);
        text = CsiRegex().Replace(text, string.Empty);
        text = OtherEscapeRegex().Replace(text, string.Empty);

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\n' || ch == '\t' || ch >= ' ')
            {
                sb.Append(ch);
            }
            else if (ch == '\r')
            {
                // Normalize CR / CRLF to a single LF.
                if (sb.Length == 0 || sb[^1] != '\n')
                {
                    sb.Append('\n');
                }
            }

            // Other C0 control chars are dropped.
        }

        return sb.ToString();
    }
}
