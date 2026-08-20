using System.Text.RegularExpressions;

namespace Deluno.Quality;

/// <summary>
/// What a release or file name says about the file behind it.
///
/// The library list has always offered sorting and searching on codec, audio,
/// release group and path. None of it worked, because nothing ever populated
/// those fields — the UI read them out of a provider metadata blob, and no
/// provider knows what codec your copy of a film uses. This is where they come
/// from instead.
///
/// Parsed from the name rather than probed from the file, deliberately:
/// scene and P2P naming carries this information by convention, parsing costs
/// nothing, and it works on a network share that would be expensive to read.
/// Where a value is genuinely a measurement rather than a claim — a true
/// bitrate, a real duration — it is not invented here; see
/// <see cref="MediaFileFacts.ApproximateBitrateMbps"/>, which is derived and
/// named so nobody mistakes it for a measurement.
/// </summary>
public static class MediaFileNameFacts
{
    // Ordered: the first match wins, so the more specific pattern comes first
    // (h265 before h264 would be wrong, x265/HEVC before x264 is not — they are
    // disjoint — but av1 has to precede a bare "av" style match if one is ever
    // added).
    private static readonly (Regex Pattern, string Value)[] VideoCodecs =
    [
        (Build(@"(av1)"), "AV1"),
        (Build(@"(x265|h\.?265|hevc)"), "HEVC"),
        (Build(@"(x264|h\.?264|avc)"), "H.264"),
        (Build(@"(xvid)"), "XviD"),
        (Build(@"(divx)"), "DivX"),
        (Build(@"(mpeg-?2)"), "MPEG-2"),
        (Build(@"(vp9)"), "VP9")
    ];

    private static readonly (Regex Pattern, string Value)[] AudioCodecs =
    [
        (BuildAudio(@"(truehd)"), "TrueHD"),
        (BuildAudio(@"(atmos)"), "Atmos"),
        (BuildAudio(@"(dts-?hd(\W?ma)?)"), "DTS-HD"),
        (BuildAudio(@"(dts-?x)"), "DTS:X"),
        (BuildAudio(@"(dts)"), "DTS"),
        (BuildAudio(@"(e-?ac-?3|ddp|dd\+|eac3)"), "E-AC-3"),
        (BuildAudio(@"(ac-?3|dd)"), "AC-3"),
        (BuildAudio(@"(flac)"), "FLAC"),
        (BuildAudio(@"(opus)"), "Opus"),
        (BuildAudio(@"(aac)"), "AAC"),
        (BuildAudio(@"(mp3)"), "MP3")
    ];

    /// <summary>
    /// A channel layout is two digits with a separator between them — 5.1, 7 1,
    /// 2.0 — and never two digits run together.
    ///
    /// That separator is the whole guard. Without it "2016" reads as a 2.0
    /// layout and every film from that year claims stereo; with it, a year, a
    /// resolution and a bitrate cannot be mistaken for one. The digits either
    /// side must not themselves be digits, which is what keeps "2016.2160p"
    /// from yielding "6.2".
    /// </summary>
    private static readonly Regex AudioChannelsPattern =
        new(@"(?<!\d)(?<channels>[1-9])[.\s_](?<sub>[0-2])(?!\d)", RegexOptions.Compiled);

    /// <summary>
    /// The trailing <c>-GROUP</c> convention. Anchored at the end because a
    /// hyphen anywhere else in a title is just a hyphen, and bounded in length
    /// because "Spider-Man" is not a release group.
    /// </summary>
    private static readonly Regex ReleaseGroupPattern =
        new(@"-(?<group>[A-Za-z0-9][A-Za-z0-9_.]{1,24})$", RegexOptions.Compiled);

    /// <summary>
    /// Tokens that look like a release group but are qualities, codecs or
    /// containers. Without this, "Some.Film.2019.1080p-WEB" would report a
    /// release group of "WEB".
    /// </summary>
    private static readonly HashSet<string> NotReleaseGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "web", "webdl", "web-dl", "webrip", "bluray", "blu-ray", "bdrip", "brrip", "hdtv", "sdtv",
        "dvd", "dvdrip", "remux", "proper", "repack", "extended", "uncut", "internal",
        "1080p", "720p", "2160p", "480p", "4k", "uhd", "hdr", "hdr10", "dv", "sdr",
        "x264", "x265", "h264", "h265", "hevc", "av1", "xvid", "divx",
        "aac", "ac3", "eac3", "dts", "truehd", "atmos", "flac", "opus", "mp3",
        "mkv", "mp4", "avi", "ts", "m4v",
        // The tail of a hyphenated source token: WEB-DL, BLU-RAY, E-AC3.
        "dl", "ray", "ac3", "hd", "ma", "x", "rip"
    };

    private static Regex Build(string pattern)
        => new($@"(?<![A-Za-z0-9]){pattern}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Like <see cref="Build"/>, but a digit may follow. Audio tokens are
    /// routinely welded to their channel count — DDP5.1, AAC2.0, DD2.0 — so
    /// refusing a trailing digit misses most real names.
    /// </summary>
    private static Regex BuildAudio(string pattern)
        => new($@"(?<![A-Za-z0-9]){pattern}(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extensions worth stripping before parsing.
    ///
    /// Explicitly listed rather than using Path.GetFileNameWithoutExtension,
    /// which on a name like "Arrival.2016.1080p.BluRay.x264-SPARKS" treats
    /// "-SPARKS" as the extension and removes the release group, the codec and
    /// half the name with it. Release names are full of dots; only a real media
    /// extension is an extension.
    /// </summary>
    private static readonly string[] MediaExtensions =
    [
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".webm"
    ];

    /// <summary>
    /// Reads what the name claims. Every field is independently optional: a
    /// name that says nothing about audio still yields its video codec.
    /// </summary>
    public static MediaFileFacts Parse(string? releaseOrFileName)
    {
        if (string.IsNullOrWhiteSpace(releaseOrFileName))
        {
            return MediaFileFacts.Empty;
        }

        var name = StripPathAndExtension(releaseOrFileName.Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            return MediaFileFacts.Empty;
        }

        return new MediaFileFacts(
            VideoCodec: MatchFirst(VideoCodecs, name),
            AudioCodec: MatchFirst(AudioCodecs, name),
            AudioChannels: ParseChannels(name),
            ReleaseGroup: ParseReleaseGroup(name));
    }

    private static string StripPathAndExtension(string value)
    {
        var name = value;
        var separator = name.LastIndexOfAny(['/', '\\']);
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        foreach (var extension in MediaExtensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^extension.Length];
            }
        }

        return name;
    }

    private static string? MatchFirst((Regex Pattern, string Value)[] candidates, string name)
    {
        foreach (var (pattern, value) in candidates)
        {
            if (pattern.IsMatch(name))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ParseChannels(string name)
    {
        foreach (Match match in AudioChannelsPattern.Matches(name))
        {
            var channels = match.Groups["channels"].Value;
            var sub = match.Groups["sub"].Value;

            // 5.1, 7.1, 2.0 are channel layouts. 1080p's "0" and a year's
            // digits are not, which is what the surrounding boundaries and the
            // restricted subwoofer digit are for.
            if (channels is "1" or "2" or "5" or "6" or "7" or "9" && sub is "0" or "1" or "2")
            {
                return $"{channels}.{sub}";
            }
        }

        return null;
    }

    private static string? ParseReleaseGroup(string name)
    {
        var match = ReleaseGroupPattern.Match(name);
        if (!match.Success)
        {
            return null;
        }

        var group = match.Groups["group"].Value.Trim('.', '_');
        return group.Length == 0 || NotReleaseGroups.Contains(group) ? null : group;
    }
}

/// <summary>
/// The facts a file name yields, plus the one number that can honestly be
/// derived from them.
/// </summary>
public sealed record MediaFileFacts(
    string? VideoCodec,
    string? AudioCodec,
    string? AudioChannels,
    string? ReleaseGroup)
{
    public static MediaFileFacts Empty { get; } = new(null, null, null, null);

    /// <summary>
    /// Size and duration give an average bitrate, and nothing else Deluno holds
    /// does. It is an average over the whole file, so it is not the peak and it
    /// is not what a media probe would report for the video stream alone —
    /// which is why it says "approximate" in the name and should keep saying so
    /// wherever it is shown.
    ///
    /// Returns <c>null</c> rather than a guess when either input is missing.
    /// </summary>
    public static double? ApproximateBitrateMbps(long? fileSizeBytes, int? runtimeMinutes)
    {
        if (fileSizeBytes is not > 0 || runtimeMinutes is not > 0)
        {
            return null;
        }

        var seconds = runtimeMinutes.Value * 60d;
        return Math.Round(fileSizeBytes.Value * 8d / seconds / 1_000_000d, 2);
    }
}
