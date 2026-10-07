using System.Text.RegularExpressions;

namespace Deluno.Quality;

/// <summary>
/// The quality a release's name claims, as search judges it (#610).
///
/// <para>Search used to read any name it did not recognise as WEB, and any
/// name without a resolution as 1080p, so a DVDRip, a BDRip and an Internet
/// Archive "Featurettes" upload all passed a WEB 1080p cutoff. This reads what
/// the name says and admits what it does not: a resolution with no source is
/// the lowest source at that resolution (HDTV, as Sonarr and Radarr read it),
/// and a name that gives Deluno nothing to go on is Unknown, which no profile
/// takes as its target.</para>
/// </summary>
public static partial class ReleaseNameQuality
{
    public const string Unknown = "Unknown";

    public static string Read(string? releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return Unknown;
        }

        var name = releaseName;
        if (Cam().IsMatch(name)) return "CAM";
        if (Telesync().IsMatch(name)) return "TELESYNC";
        var resolution = ResolutionOf(name);
        var source = SourceOf(name);

        // What the shared detector knows, it decides: CAM and the other
        // low-grade sources, disc images, and the source-and-resolution pairs
        // it spells out.
        var detected = LibraryQualityDecider.DetectQuality(name);
        if (detected is not null && (source is null || !detected.StartsWith("WEB", StringComparison.Ordinal) || source == "WEB"))
        {
            return detected;
        }

        return (source, resolution) switch
        {
            ("Remux", "2160p" or "1080p") => $"Remux {resolution}",
            ("Remux", _) => "Remux 1080p",
            ("Bluray", { } res) => res is "2160p" or "1080p" or "720p" or "576p" or "480p" ? $"Bluray {res}" : Unknown,
            ("WEB", { } res) => res is "2160p" or "1080p" or "720p" or "480p" ? $"WEB {res}" : res == "576p" ? "WEB 480p" : Unknown,
            ("HDTV", { } res) => res is "2160p" or "1080p" or "720p" ? $"HDTV {res}" : "SDTV",
            ("HDTV", null) => "SDTV",
            ("DVD", _) => "DVD",
            (null, "2160p" or "1080p" or "720p") => $"HDTV {resolution}",
            (null, "576p" or "480p") => "SDTV",
            _ => Unknown
        };
    }

    /// <summary>
    /// The resolution a release's name claims, and the least picture it
    /// requires: a width OR a height reaches the minimum (#1148). Width alone
    /// keeps a cropped widescreen 3840&#215;1600 passing as 2160p (#917);
    /// height alone keeps a 4:3 film (1440&#215;1080) passing as 1080p. A
    /// picture below both is not what the name says. Null means the name says
    /// nothing Deluno can check a picture against.
    /// </summary>
    public static (string Label, int MinimumWidth, int MinimumHeight)? ClaimedResolution(string? releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return null;
        }

        return ResolutionOf(releaseName) switch
        {
            "2160p" => ("2160p", 3200, 2000),
            "1080p" => ("1080p", 1600, 1000),
            "720p" => ("720p", 1100, 680),
            "576p" => ("576p", 700, 540),
            "480p" => ("480p", 600, 450),
            _ => null
        };
    }

    /// <summary>
    /// Whether a picture of this size is at least the claimed resolution: its
    /// width or its height reaches the minimum (#1148). So 1440&#215;1080,
    /// 1920&#215;800 and 1920&#215;1080 are all 1080p, and 1280&#215;720 is not.
    /// </summary>
    public static bool MeetsClaim((string Label, int MinimumWidth, int MinimumHeight) claim, int width, int height)
        => width >= claim.MinimumWidth || height >= claim.MinimumHeight;

    private static string? ResolutionOf(string name)
    {
        if (Uhd().IsMatch(name)) return "2160p";
        if (FullHd().IsMatch(name)) return "1080p";
        if (Hd().IsMatch(name)) return "720p";
        if (Pal().IsMatch(name)) return "576p";
        if (Sd().IsMatch(name)) return "480p";
        return null;
    }

    private static string? SourceOf(string name)
    {
        if (Remux().IsMatch(name)) return "Remux";
        if (Bluray().IsMatch(name)) return "Bluray";
        if (Web().IsMatch(name)) return "WEB";
        if (Tv().IsMatch(name)) return "HDTV";
        if (Dvd().IsMatch(name)) return "DVD";
        return null;
    }

    // HDCAM and HDTS are how cam and telesync copies are usually named.
    [GeneratedRegex(@"(?<![a-z])(hd-?cam|camrip|cam)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex Cam();

    [GeneratedRegex(@"(?<![a-z])(hd-?ts|telesync|ts)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex Telesync();

    [GeneratedRegex(@"(?<![a-z0-9])(2160p?|4k|uhd)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Uhd();

    [GeneratedRegex(@"(1080[pi]?|fhd)(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex FullHd();

    [GeneratedRegex(@"(?<![0-9])720[p]?(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Hd();

    [GeneratedRegex(@"(?<![0-9])576[pi]?(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Pal();

    [GeneratedRegex(@"(?<![0-9])480[pi]?(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Sd();

    [GeneratedRegex(@"(?<![a-z])(bd-?remux|remux)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex Remux();

    // BD1080p, BDRip, BRRip, Blu-ray, BluRay: every way a Blu-ray source is written.
    [GeneratedRegex(@"(?<![a-z])(blu-?ray|bdrip|brrip|bd(?=[\s._-]?\d{3,4}p)|bd(?![a-z]))", RegexOptions.IgnoreCase)]
    private static partial Regex Bluray();

    [GeneratedRegex(@"(?<![a-z])(web-?dl|webrip|web|hdrip|amzn|nf|hmax|dsnp|atvp)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex Web();

    [GeneratedRegex(@"(?<![a-z])(hdtv|pdtv|sdtv|dsr|tvrip)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex Tv();

    [GeneratedRegex(@"(?<![a-z])(dvdrip|dvd|dvd-?r|dvd5|dvd9)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex Dvd();
}
