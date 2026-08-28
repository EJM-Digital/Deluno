using System.IO.Compression;
using System.Text;

namespace Deluno.Integrations.Subtitles;

/// <summary>
/// Getting a subtitle out of whatever a provider actually sent.
///
/// <para>Half of them return a zip and none of them say so in a header worth
/// trusting, so this sniffs the bytes. Weir had the same routine copied into
/// three provider clients and inlined in a fourth; it is one function here for
/// the usual reason.</para>
/// </summary>
public static class SubtitleArchive
{
    /// <summary>The subtitle extensions Deluno will accept out of an archive.</summary>
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".sub", ".vtt"];

    /// <summary>PK\x03\x04 — the local file header every zip starts with.</summary>
    private static bool LooksLikeZip(byte[] data)
        => data.Length > 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04;

    /// <summary>
    /// The subtitle inside, or the bytes unchanged when they already are one.
    ///
    /// <para>Prefers <c>.srt</c> over the other formats when an archive holds
    /// several, because it is the one every player reads. Returns null when the
    /// archive holds no subtitle at all, which is a provider handing back
    /// something else entirely and has to be reported rather than written to
    /// disk.</para>
    /// </summary>
    public static byte[]? Extract(byte[] data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        if (!LooksLikeZip(data))
        {
            return data;
        }

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var entry =
                archive.Entries.FirstOrDefault(item => item.FullName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                ?? archive.Entries.FirstOrDefault(item =>
                    SubtitleExtensions.Any(extension => item.FullName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));

            if (entry is null)
            {
                return null;
            }

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (InvalidDataException)
        {
            // A zip header on something that is not a zip. Nothing to write.
            return null;
        }
    }

    /// <summary>
    /// Whether the bytes read as a subtitle rather than as an error page.
    ///
    /// <para>Providers return HTML far more often than they return a 4xx: a
    /// rate limit, a captcha, a "sign in to download" page, all with a 200. A
    /// file called <c>Film.en.srt</c> containing <c>&lt;!DOCTYPE html&gt;</c> is
    /// worse than no file at all, because the bar goes green and the player
    /// shows nothing.</para>
    /// </summary>
    public static bool LooksLikeSubtitle(byte[] data)
    {
        if (data.Length < 8)
        {
            return false;
        }

        var head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 512)).TrimStart('﻿', ' ', '\r', '\n', '\t');

        if (head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith('{'))
        {
            return false;
        }

        // SubStation opens with a section header, and so does a JSON array as
        // far as the first character is concerned. The section is named, so say
        // which one rather than rejecting every `.ass` to be rid of `[{"error"`.
        if (head.StartsWith('['))
        {
            return head.Contains("Script Info", StringComparison.OrdinalIgnoreCase)
                || head.Contains("V4 Styles", StringComparison.OrdinalIgnoreCase)
                || head.Contains("V4+ Styles", StringComparison.OrdinalIgnoreCase)
                || head.Contains("Events", StringComparison.OrdinalIgnoreCase);
        }

        // SubRip opens with a cue number and WebVTT with its signature. Anything
        // else is not a subtitle we are prepared to hand a player.
        return head.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
            || head.Contains("-->", StringComparison.Ordinal)
            || char.IsDigit(head[0]);
    }
}
