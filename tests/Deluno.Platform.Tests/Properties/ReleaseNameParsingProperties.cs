using Deluno.Contracts;
using Deluno.Quality;
using Deluno.Tests.Support;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static Deluno.Tests.Support.ReleaseNameGenerators;

namespace Deluno.Platform.Tests.Properties;

/// <summary>
/// #1271: the release-name parser (title, year, quality, source, codec, grp), as properties over
/// names built from known parts. Each test name states the invariant.
/// </summary>
public sealed class ReleaseNameParsingProperties
{
    private sealed record Film(string[] Words, int Year, string Separator, string Resolution, string Source, string Codec, string Group)
    {
        public string Title => string.Join(' ', Words);

        public string Name => string.Join(Separator, [.. Words, Year.ToString(), Resolution, Source, Codec]) + "-" + Group;
    }

    private static Arbitrary<Film> Films()
        => (from words in Words()
            from year in Year()
            from separator in Separator()
            from resolution in Resolution()
            from source in Source()
            from codec in Codec()
            from grp in Group()
            select new Film(words, year, separator, resolution, source, codec, grp)).ToArbitrary();

    [Fact]
    public void The_title_and_year_read_from_a_name_are_the_title_and_year_it_was_built_from()
        => Prop.ForAll(Films(), film =>
        {
            var (title, year) = ReleaseTitleMatch.ParseTitleAndYear(film.Name);
            return title == film.Title && year == film.Year;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void The_title_and_year_do_not_depend_on_the_case_of_the_name()
        => Prop.ForAll(Films(), film =>
        {
            var (title, year) = ReleaseTitleMatch.ParseTitleAndYear(film.Name.ToUpperInvariant());
            return string.Equals(title, film.Title, StringComparison.OrdinalIgnoreCase) && year == film.Year;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_year_in_brackets_or_parentheses_reads_the_same_as_a_bare_year()
        => Prop.ForAll(Films(), film =>
        {
            var bare = ReleaseTitleMatch.ParseTitleAndYear($"{film.Title} {film.Year} {film.Resolution}");
            var round = ReleaseTitleMatch.ParseTitleAndYear($"{film.Title} ({film.Year}) [{film.Resolution}]");
            var square = ReleaseTitleMatch.ParseTitleAndYear($"{film.Title} [{film.Year}] {film.Resolution}");
            return bare == round && bare == square;
        }).QuickCheckThrowOnFailure();

    [Property]
    public bool ParseTitleAndYear_never_throws_and_always_gives_a_title_for_any_text(NonEmptyString text)
    {
        var (title, year) = ReleaseTitleMatch.ParseTitleAndYear(text.Get);
        return title.Length > 0 && (year is null or >= 1900 and <= 2099);
    }

    [Fact]
    public void The_release_group_is_the_text_after_the_last_dash()
        => Prop.ForAll(Films(), film => MediaFileNameFacts.Parse(film.Name).ReleaseGroup == film.Group).QuickCheckThrowOnFailure();

    [Fact]
    public void The_video_codec_read_is_the_codec_family_the_name_carries()
        => Prop.ForAll(Films(), film =>
        {
            var codec = MediaFileNameFacts.Parse(film.Name).VideoCodec;
            return film.Codec is "x265" or "HEVC"
                ? codec is not null && (codec.Contains("265") || codec.Contains("HEVC", StringComparison.OrdinalIgnoreCase))
                : codec is not null && codec.Contains("264");
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void The_source_read_does_not_change_when_the_name_is_in_other_case()
        => Prop.ForAll(Films(), film =>
            MediaFileNameFacts.Parse(film.Name).Source == MediaFileNameFacts.Parse(film.Name.ToLowerInvariant()).Source).QuickCheckThrowOnFailure();

    [Property]
    public bool MediaFileNameFacts_never_throws_for_any_text(string? text)
    {
        _ = MediaFileNameFacts.Parse(text);
        return true;
    }

    [Property]
    public bool ReleaseNameQuality_never_throws_and_always_names_a_quality_for_any_text(string? text)
        => !string.IsNullOrWhiteSpace(ReleaseNameQuality.Read(text));

    [Fact]
    public void The_quality_read_from_a_blu_ray_name_carries_the_resolution_in_the_name()
        => Prop.ForAll(Films().Filter(f => f.Source == "BluRay" && f.Resolution != "576p"), film =>
        {
            var quality = ReleaseNameQuality.Read(film.Name);
            return quality.Contains(film.Resolution, StringComparison.Ordinal);
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void The_quality_read_is_the_same_in_any_case()
        => Prop.ForAll(Films(), film =>
            ReleaseNameQuality.Read(film.Name) == ReleaseNameQuality.Read(film.Name.ToUpperInvariant())
            && ReleaseNameQuality.Read(film.Name) == ReleaseNameQuality.Read(film.Name.ToLowerInvariant())).QuickCheckThrowOnFailure();

    [Fact]
    public void A_higher_resolution_of_the_same_source_never_ranks_lower()
    {
        string[] resolutions = ["480p", "720p", "1080p", "2160p"];
        foreach (var source in new[] { "BluRay", "WEB-DL", "HDTV" })
        {
            var ranks = resolutions
                .Select(res => LibraryQualityDecider.GetRank(ReleaseNameQuality.Read($"Night.Of.The.Living.Dead.1968.{res}.{source}.x264-GRP")))
                .ToArray();
            Assert.Equal(ranks.OrderBy(rank => rank), ranks);
        }
    }

    [Fact]
    public void The_resolution_a_name_claims_is_the_resolution_in_it_and_a_picture_of_that_size_meets_it()
        => Prop.ForAll(Films().Filter(f => f.Resolution != "576p"), film =>
        {
            var claim = ReleaseNameQuality.ClaimedResolution(film.Name);
            return claim is { } c && c.Label == film.Resolution && ReleaseNameQuality.MeetsClaim(c, c.MinimumWidth, 1) ;
        }).QuickCheckThrowOnFailure();

    [Property]
    public bool A_picture_that_meets_a_claim_still_meets_it_when_it_is_larger(PositiveInt width, PositiveInt height, PositiveInt extraWidth, PositiveInt extraHeight)
        => ReleaseNameQuality.ClaimedResolution("Film.2020.1080p.WEB-DL") is not { } claim
           || !ReleaseNameQuality.MeetsClaim(claim, width.Get, height.Get)
           || ReleaseNameQuality.MeetsClaim(claim, width.Get + extraWidth.Get, height.Get + extraHeight.Get);
}
