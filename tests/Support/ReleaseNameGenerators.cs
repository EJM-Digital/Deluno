using FsCheck;
using FsCheck.Fluent;

namespace Deluno.Tests.Support;

/// <summary>
/// Generators for the property tests (#1271): release names built from parts, so a property can say
/// what the name was made from and check a parser gets it back.
/// </summary>
internal static class ReleaseNameGenerators
{
    /// <summary>Words that are not a quality, source, codec or marker word, and hold no digit.</summary>
    public static readonly string[] TitleWords =
    [
        "Night", "Living", "Dead", "Blade", "Runner", "Kid", "House", "Haunted", "Hill", "Alien", "Garden",
        "Winter", "Silver", "Harbor", "Northern", "Lights", "Crimson", "Tide", "Paper", "Moon", "Iron", "River"
    ];

    public static readonly string[] Resolutions = ["480p", "576p", "720p", "1080p", "2160p"];

    public static readonly string[] Sources = ["BluRay", "WEB-DL", "WEBRip", "HDTV", "DVDRip"];

    public static readonly string[] Codecs = ["x264", "x265", "H.264", "HEVC"];

    public static readonly string[] Groups = ["GRP", "NTb", "FLUX", "DELUNO", "YIFY", "SPARKS"];

    public static readonly string[] Separators = [".", " ", "_"];

    public static Gen<string[]> Words(int min = 1, int max = 4)
        => from count in Gen.Choose(min, max)
           from words in Gen.Elements(TitleWords).ArrayOf(count)
           select words;

    public static Gen<string> Separator() => Gen.Elements(Separators);

    public static Gen<int> Year() => Gen.Choose(1920, 2030);

    public static Gen<string> Resolution() => Gen.Elements(Resolutions);

    public static Gen<string> Source() => Gen.Elements(Sources);

    public static Gen<string> Codec() => Gen.Elements(Codecs);

    public static Gen<string> Group() => Gen.Elements(Groups);
}
