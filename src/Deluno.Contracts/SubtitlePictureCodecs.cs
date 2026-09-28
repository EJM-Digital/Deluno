namespace Deluno.Contracts;

/// <summary>
/// Which embedded subtitle codecs are pictures, not text (#988). PGS
/// (Blu-ray) and VobSub (DVD) tracks are a stream of bitmap images with no
/// text a provider's release could ever match against, so a text subtitle
/// fetched to sit beside one is never a duplicate of what is already there —
/// unlike an embedded SubRip or ASS track, which really does carry the same
/// words a provider would fetch.
///
/// <para>Read from <c>MediaSubtitleRow.Codec</c>/<c>sub.codec</c>, which for an
/// embedded track is ffprobe's own <c>codec_name</c> — lowercase, exactly as
/// the container reports it, the same raw value Deluno's video/audio codec
/// tables (<c>MediaProbedFacts</c>) key off elsewhere.</para>
/// </summary>
public static class SubtitlePictureCodecs
{
    /// <summary>ffprobe's name for a Blu-ray PGS track.</summary>
    public const string Pgs = "hdmv_pgs_subtitle";

    /// <summary>ffprobe's name for a DVD VobSub track.</summary>
    public const string VobSub = "dvd_subtitle";

    private static readonly HashSet<string> PictureCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        Pgs,
        VobSub,
        // Alternate spellings other tools and older ffmpeg builds have used
        // for the same two formats.
        "pgssub",
        "dvdsub",
        "vobsub"
    };

    public static bool IsPictureBased(string? codec)
        => !string.IsNullOrWhiteSpace(codec) && PictureCodecs.Contains(codec.Trim());

    /// <summary>Every recognised codec name, for a caller building a SQL <c>IN</c> list rather than testing one value.</summary>
    public static readonly IReadOnlyList<string> All = [.. PictureCodecs];
}
