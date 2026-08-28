using System.Globalization;
using System.Text;

namespace Deluno.Filesystem.Subtitles;

/// <summary>
/// One cue: when it appears, when it goes, and what it says.
/// </summary>
/// <param name="Start">From the start of the film.</param>
/// <param name="End">From the start of the film.</param>
/// <param name="Text">The lines as written, newlines and markup intact.</param>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// A <c>.srt</c> file, read as times rather than bytes.
///
/// <para><b>Why Deluno parses this at all.</b> Timing sync moves every cue by the
/// same amount, and to move a cue you have to know where it is. Nothing else in
/// Deluno has ever needed to look inside a subtitle — the fetcher hands bytes to
/// the writer and the scanner reads only the name — so this is the first code
/// that opens one.</para>
///
/// <para><b>Deliberately forgiving on the way in and strict on the way out.</b>
/// Subtitles come from strangers. They arrive with a byte-order mark or without,
/// with CRLF or LF, with a blank line missing between cues, with the index
/// numbers out of order or absent, and with <c>.</c> where the format says
/// <c>,</c>. A parser that refused any of those would refuse real files people
/// actually have, and the failure would look like "sync did nothing" rather than
/// "your file is unusual". What is written back is always canonical: UTF-8, CRLF,
/// renumbered from one.</para>
///
/// <para><b>Encoding.</b> A BOM is believed. Without one the bytes are tried as
/// UTF-8 strictly, and only if that fails are they read as Windows-1252 — which
/// cannot fail and is what the great majority of non-UTF-8 subtitles in the wild
/// actually are. Getting this wrong is not subtle: it turns every accented
/// character into a question mark, in a file somebody then has to read for two
/// hours.</para>
/// </summary>
public static class SubtitleTimeline
{
    /// <summary>
    /// The separator between cues on the way out. CRLF because that is what the
    /// format's own examples use and what the oldest hardware players expect;
    /// nothing reading an <c>.srt</c> objects to it.
    /// </summary>
    private const string LineEnding = "\r\n";

    /// <summary>
    /// Reads the cues out of a subtitle's bytes.
    ///
    /// <para>Returns an empty list rather than throwing when the bytes are not a
    /// subtitle at all — an image-based <c>.sup</c> that arrived with the wrong
    /// extension, or an HTML error page a provider returned instead of a file.
    /// The caller's answer to both is the same: leave it alone.</para>
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Parse(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return [];
        }

        var text = Decode(bytes);
        var cues = new List<SubtitleCue>();

        // Split on line boundaries rather than on blank-line-separated blocks: a
        // missing blank line between two cues is common enough that blocks would
        // silently swallow the pair, and a timing line is unambiguous enough to
        // find on its own.
        var lines = text.Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index].TrimEnd('\r');
            if (!TryParseTiming(line, out var start, out var end))
            {
                index++;
                continue;
            }

            index++;
            var body = new StringBuilder();
            while (index < lines.Length)
            {
                var next = lines[index].TrimEnd('\r');
                if (next.Length == 0 || TryParseTiming(next, out _, out _))
                {
                    break;
                }

                // A lone number on its own line, immediately before a timing
                // line, is the next cue's index rather than this cue's last
                // line of dialogue.
                if (IsIndexLine(next) && index + 1 < lines.Length && TryParseTiming(lines[index + 1].TrimEnd('\r'), out _, out _))
                {
                    break;
                }

                if (body.Length > 0)
                {
                    body.Append('\n');
                }

                body.Append(next);
                index++;
            }

            cues.Add(new SubtitleCue(start, end, body.ToString()));
        }

        return cues;
    }

    /// <summary>
    /// Moves every cue by the same amount and hands back the file to write.
    ///
    /// <para>A cue pushed before zero is slid back to zero rather than dropped.
    /// Losing a line of dialogue to make the arithmetic tidy is the wrong trade —
    /// it affects only the handful of cues in the opening seconds, and a
    /// subtitle that appears a moment early is a far smaller problem than one
    /// that is not there.</para>
    ///
    /// <para><b>It keeps its length while it does that</b>, which the first
    /// version did not: clamping the start and the end independently turned
    /// every cue in the first few seconds into <c>00:00:00 --&gt; 00:00:00</c>, a
    /// cue no player displays. That is dropping the line while claiming not to,
    /// which is worse than either honest choice.</para>
    /// </summary>
    public static byte[] Shift(IReadOnlyList<SubtitleCue> cues, TimeSpan offset)
        => Render([.. cues.Select(cue => Slide(cue, offset))]);

    private static SubtitleCue Slide(SubtitleCue cue, TimeSpan offset)
    {
        var start = cue.Start + offset;
        var end = cue.End + offset;

        if (start < TimeSpan.Zero)
        {
            // Both ends move together, so the line stays on screen for exactly
            // as long as it was meant to.
            end -= start;
            start = TimeSpan.Zero;
        }

        return cue with { Start = start, End = end };
    }

    public static byte[] Render(IReadOnlyList<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            builder.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(LineEnding);
            builder.Append(Format(cue.Start)).Append(" --> ").Append(Format(cue.End)).Append(LineEnding);
            builder.Append(cue.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", LineEnding, StringComparison.Ordinal)).Append(LineEnding);
            builder.Append(LineEnding);
        }

        // No BOM. It is optional in UTF-8, some older players show it as a stray
        // glyph on the first cue, and every player reads a BOM-less UTF-8 file.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(builder.ToString());
    }

    private static string Format(TimeSpan value)
        => string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}");

    private static bool IsIndexLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// <c>00:01:02,500 --&gt; 00:01:04,000</c>, and the several ways real files
    /// write that.
    /// </summary>
    private static bool TryParseTiming(string line, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;

        var arrow = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return false;
        }

        // Everything after the end stamp is position data (X1:.. Y1:..) that
        // Deluno neither reads nor writes, so the second half is cut at its
        // first space.
        var right = line[(arrow + 3)..].Trim();
        var space = right.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0)
        {
            right = right[..space];
        }

        return TryParseStamp(line[..arrow].Trim(), out start) && TryParseStamp(right, out end);
    }

    private static bool TryParseStamp(string value, out TimeSpan stamp)
    {
        stamp = default;
        if (value.Length == 0)
        {
            return false;
        }

        // Some writers use a full stop for the fractional separator, and some
        // omit the hour entirely.
        var normalised = value.Replace('.', ',');
        var comma = normalised.LastIndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        var clock = normalised[..comma].Split(':');
        if (clock.Length is < 2 or > 3)
        {
            return false;
        }

        var hours = 0;
        var offset = 0;
        if (clock.Length == 3)
        {
            if (!int.TryParse(clock[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
            {
                return false;
            }

            offset = 1;
        }

        if (!int.TryParse(clock[offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(clock[offset + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(normalised[(comma + 1)..].PadRight(3, '0')[..3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return false;
        }

        stamp = new TimeSpan(0, hours, minutes, seconds, milliseconds);
        return true;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Windows-1252 rather than Latin-1: they differ only in the range
            // 0x80–0x9F, which is exactly where curly quotes and dashes live,
            // and those appear in nearly every subtitle written on Windows.
            return Windows1252(bytes);
        }
    }

    /// <summary>
    /// Windows-1252, by hand.
    ///
    /// <para>.NET Core dropped the legacy code pages; getting this encoding back
    /// means taking a dependency on <c>System.Text.Encoding.CodePages</c> and
    /// remembering to register its provider at startup — a whole package, and a
    /// line in a composition root a long way from here, for thirty-two
    /// characters. The table below <i>is</i> those thirty-two: every other byte
    /// in Windows-1252 is its own Unicode code point.</para>
    /// </summary>
    private static string Windows1252(byte[] bytes)
        => string.Create(bytes.Length, bytes, (destination, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var value = source[i];
                destination[i] = value is >= 0x80 and <= 0x9F
                    ? HighRange[value - 0x80]
                    : (char)value;
            }
        });

    /// <summary>
    /// Bytes 0x80–0x9F, in order. Every other byte in Windows-1252 is its
    /// own Unicode code point, so this table is the whole of the difference.
    /// The five bytes Windows-1252 leaves undefined map to the replacement
    /// character, so a wrongly detected file shows a visible box rather than an
    /// invisible control code.
    /// </summary>
    private const string HighRange =
        "€�‚ƒ„…†‡" +
        "ˆ‰Š‹Œ�Ž�" +
        "�‘’“”•–—" +
        "˜™š›œ�žŸ";
}
