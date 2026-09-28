using System.Linq;

namespace Deluno.Quality;

/// <summary>
/// What the file itself says, as opposed to what its name claims.
///
/// <para><b>Why this exists.</b> <see cref="MediaFileNameFacts"/> reads the
/// codec and the audio layout out of the release name, which works because
/// scene and P2P naming carries them by convention. A library that was renamed
/// on the way in carries nothing: on the rig, <c>Big Buck Bunny (2008).mkv</c>
/// yields no codec, no audio, no channels, and the Codec switch draws a dash on
/// every card. James: <i>"not all the switches work… everything is broken and
/// you need to fix it all properly"</i>.</para>
///
/// <para><b>One vocabulary, two sources.</b> ffprobe says <c>h264</c> and a
/// release name says <c>x264</c>; both have to become <c>H.264</c> or a filter
/// finds half a library. The mapping here lands on exactly the strings
/// <see cref="MediaFileNameFacts"/> produces, and a test walks both lists to
/// prove no source can invent a word the other cannot.</para>
///
/// <para><b>What a probe cannot answer.</b> The release group is a naming
/// convention and nothing inside the container records it, so it stays
/// name-only. Saying otherwise would be a switch that still draws a dash and a
/// claim that it had been fixed.</para>
/// </summary>
public static partial class MediaProbedFacts
{
    /// <summary>
    /// ffprobe's codec name to the word the rest of Deluno uses.
    ///
    /// <para>Unknown codecs come back uppercased rather than dropped: a file in
    /// something exotic should read as that thing, not as nothing. It is a
    /// measurement either way.</para>
    /// </summary>
    public static string? VideoCodec(string? probed)
    {
        var name = probed?.Trim().ToLowerInvariant();

        return name switch
        {
            null or "" => null,
            "h264" or "avc" or "avc1" => "H.264",
            "hevc" or "h265" => "HEVC",
            "av1" => "AV1",
            "vp9" => "VP9",
            "mpeg2video" => "MPEG-2",
            "msmpeg4v3" or "div3" => "DivX",
            "mpeg4" => "XviD",
            _ => name.ToUpperInvariant()
        };
    }

    /// <summary>
    /// The audio codec, taking the profile into account where it changes the
    /// answer.
    /// </summary>
    /// <param name="profile">
    /// DTS-HD Master Audio and plain DTS are both <c>dts</c> to ffprobe and are
    /// not the same thing to anybody choosing a file, so the profile is what
    /// separates them. Same for TrueHD with Atmos.
    /// </param>
    public static string? AudioCodec(string? probed, string? profile)
    {
        var name = probed?.Trim().ToLowerInvariant();
        var detail = profile?.Trim().ToLowerInvariant() ?? string.Empty;

        return name switch
        {
            null or "" => null,
            "truehd" => detail.Contains("atmos") ? "Atmos" : "TrueHD",
            "dts" when detail.Contains("dts-hd ma") || detail.Contains("master audio") => "DTS-HD",
            "dts" when detail.Contains("dts:x") || detail.Contains("dts-x") => "DTS:X",
            "dts" => "DTS",
            "eac3" => "E-AC-3",
            "ac3" => "AC-3",
            "flac" => "FLAC",
            "opus" => "Opus",
            "aac" => "AAC",
            "mp3" => "MP3",
            _ => name.ToUpperInvariant()
        };
    }

    /// <summary>
    /// The channel layout as a person writes it — 5.1, 7.1, 2.0.
    ///
    /// <para>ffprobe reports a layout like <c>5.1(side)</c> and a count like
    /// <c>6</c>. The layout is preferred and stripped of its parenthetical,
    /// because <c>5.1(side)</c> and <c>5.1</c> are the same answer to the
    /// question being asked; the count is the fallback, since a file with six
    /// channels and no layout is still 5.1.</para>
    /// </summary>
    public static string? AudioChannels(string? layout, int? channels)
    {
        var stated = layout?.Trim().ToLowerInvariant();

        if (!string.IsNullOrEmpty(stated))
        {
            var bare = stated.Split('(')[0].Trim();

            // "stereo" and "mono" are layouts, not numbers, and the shelf asks
            // in numbers.
            return bare switch
            {
                "stereo" => "2.0",
                "mono" => "1.0",
                "" => FromCount(channels),
                _ => bare
            };
        }

        return FromCount(channels);
    }

    private static string? FromCount(int? channels)
        => channels switch
        {
            null or <= 0 => null,
            1 => "1.0",
            2 => "2.0",
            6 => "5.1",
            8 => "7.1",
            // Anything else is stated honestly rather than rounded to the
            // nearest familiar layout.
            _ => $"{channels}.0"
        };

    /// <summary>
    /// Colour bit depth from ffprobe's pixel format name, welded to "bit" the
    /// same way <see cref="MediaFileNameFacts"/> does: <c>10-bit</c>, not
    /// <c>10</c>.
    ///
    /// <para>ffmpeg's naming is not fully regular: <c>nv12</c> ends in "12"
    /// but is an 8-bit format (the digits name the chroma layout, not the
    /// depth), while <c>yuv420p10le</c> and <c>p010le</c> really are 10-bit.
    /// The known-format lists below exist so a chroma-layout number is never
    /// mistaken for a colour depth.</para>
    /// </summary>
    public static string? BitDepth(string? pixelFormat)
    {
        var format = pixelFormat?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(format))
        {
            return null;
        }

        if (format.StartsWith("p012", StringComparison.Ordinal))
        {
            return "12-bit";
        }

        if (format.StartsWith("p010", StringComparison.Ordinal))
        {
            return "10-bit";
        }

        var explicitDepth = ExplicitPixelFormatBitDepth().Match(format);
        if (explicitDepth.Success)
        {
            return $"{explicitDepth.Groups["bits"].Value}-bit";
        }

        // No depth suffix at all: ffmpeg's 8-bit formats never carry one
        // (yuv420p, nv12, rgb24, ...), so a recognised colour-format prefix
        // without one is honestly 8-bit rather than unknown.
        return EightBitPixelFormatPrefixes.Any(prefix => format.StartsWith(prefix, StringComparison.Ordinal))
            ? "8-bit"
            : null;
    }

    private static readonly string[] EightBitPixelFormatPrefixes =
        ["yuv", "yuvj", "nv", "rgb", "bgr", "gbr", "gray", "uyvy", "yuyv", "pal8"];

    [System.Text.RegularExpressions.GeneratedRegex(@"p(?<bits>10|12)(le|be)?$")]
    private static partial System.Text.RegularExpressions.Regex ExplicitPixelFormatBitDepth();

    /// <summary>
    /// HDR flavour from the stream's own colour metadata, in the same
    /// vocabulary <see cref="MediaFileNameFacts"/> reads off a name: HDR10+,
    /// Dolby Vision, HDR10, HDR, or nothing for SDR.
    ///
    /// <para>Dolby Vision and PQ/HDR10 share the same transfer characteristic
    /// (<c>smpte2084</c>), so side data is what tells them apart — the same
    /// way the name parser needs "dv"/"dolby vision" to outrank a bare "hdr"
    /// token. HDR10+'s per-frame metadata is not visible at the stream level
    /// ffprobe reports here, so that one still relies on the name.</para>
    /// </summary>
    public static string? DynamicRange(string? colorTransfer, IReadOnlyList<string>? sideDataTypes)
    {
        if (sideDataTypes is not null &&
            sideDataTypes.Any(type => type.Contains("dovi", StringComparison.OrdinalIgnoreCase) ||
                                       type.Contains("dolby vision", StringComparison.OrdinalIgnoreCase)))
        {
            return "Dolby Vision";
        }

        return colorTransfer?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "smpte2084" => "HDR10",
            "arib-std-b67" => "HDR",
            _ => null
        };
    }
}
