using Deluno.Contracts;
using Deluno.Quality;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #613: release names from the live trackers on 24 September 2026, checked
/// against what was searched for.
/// </summary>
public sealed class ReleaseTitleMatchTests
{
    private static readonly ReleaseTitleExpectation TheKid = new("The Kid", 1921, IsTv: false);

    [Theory]
    [InlineData("The Kid 1921 CC 1080p BluRay x264 nikt0")]
    [InlineData("The Kid [1921] 1080p")]
    [InlineData("The.Kid.1921.1080p.BluRay.x264.anoXmous")]
    [InlineData("The Kid (1921) [BluRay] [1080p] [YTS AM]")]
    [InlineData("Малыш / The Kid (1921) BDRip 1080 | Sub")]
    [InlineData("The Kid 1921 REMASTERED 1080p BluRay x264 SADPANDA[rarbg]")]
    public void The_kid_is_the_kid(string name)
        => Assert.Null(ReleaseTitleMatch.Mismatch(name, TheKid));

    [Theory]
    [InlineData("Cinema 35 - The Kid (1921)")]
    [InlineData("Charlie Chaplin  The Kid (1921) DVDRip x264 AAC MP4  TBI")]
    [InlineData("The Kid Detective 2020 1080p WEB-DL")]
    [InlineData("Karate Kid 1984 1080p BluRay")]
    public void Another_title_is_not(string name)
        => Assert.StartsWith("This release is for something else", ReleaseTitleMatch.Mismatch(name, TheKid));

    [Fact]
    public void A_remake_is_refused_by_its_year()
        => Assert.Equal(
            "This release is from 1990; Night of the Living Dead is from 1968.",
            ReleaseTitleMatch.Mismatch("Night of the Living Dead 1990 VHSRip", new ReleaseTitleExpectation("Night of the Living Dead", 1968, IsTv: false)));

    [Theory]
    [InlineData("Sherlock Jr  (1924) [BluRay] [1080p] [YTS AM]", "Sherlock Jr.", 1924)]
    [InlineData("house.on.-haunted.-hill.-1959.1080p.-blu-ray.-x-264-amiable", "House on Haunted Hill", 1959)]
    [InlineData("Blade.Runner.2049.2017.2160p.UHD.BluRay.x265", "Blade Runner 2049", 2017)]
    [InlineData("1917.2019.1080p.BluRay.x264", "1917", 2019)]
    [InlineData("Ночь живых мертвецов / Night of the Living Dead (1968) BDRip [H.264/720p]", "Night of the Living Dead", 1968)]
    [InlineData("Amelie.2001.1080p.BluRay", "Amélie", 2001)]
    [InlineData("Fast.and.Furious.2009.1080p.WEB", "Fast & Furious", 2009)]
    public void Punctuation_accents_and_years_in_titles_are_read_through(string name, string title, int year)
        => Assert.Null(ReleaseTitleMatch.Mismatch(name, new ReleaseTitleExpectation(title, year, IsTv: false)));

    // The grab that should never have happened.
    [Fact]
    public void An_anime_with_a_word_in_common_is_not_say_yes_to_the_dress()
        => Assert.StartsWith(
            "This release is for something else",
            ReleaseTitleMatch.Mismatch(
                "Kirio Fan Club S01E09 Cant Say It Wont Say It 1080p AMZN WEB-DL DUAL DDP2.0 H.264-VARYG (Kirio Fanclub, Dual-Audio)",
                new ReleaseTitleExpectation("Say Yes to the Dress", null, IsTv: true, Season: 1, Episode: 9)));

    [Theory]
    [InlineData("Pioneer One S01E01 720p x264 VODO", null)]
    [InlineData("Pioneer.One.S01E02.720p.x264-VODO", "This release is episode 2; the search was for episode 1.")]
    [InlineData("Pioneer One S02E01 720p", "This release is season 2; the search was for season 1.")]
    [InlineData("Pioneer One S01E01-E03 720p", null)]
    [InlineData("Pioneer One 2010 Season 1 Complete REDUX 720p x264 [i c]", "This is a whole-season pack; the search was for one episode.")]
    [InlineData("Pioneer One 720p", "The name does not say which episode this is.")]
    public void An_episode_search_takes_that_episode(string name, string? expected)
        => Assert.Equal(expected, ReleaseTitleMatch.Mismatch(name, new ReleaseTitleExpectation("Pioneer One", 2010, IsTv: true, Season: 1, Episode: 1)));

    [Theory]
    [InlineData("Pioneer One 2010 Season 1 Complete REDUX 720p x264 [i c]", null)]
    [InlineData("Pioneer One S01E04 720p", null)]
    [InlineData("Pioneer.One.S02.720p", "This release is season 2; the search was for season 1.")]
    public void A_season_search_takes_that_season(string name, string? expected)
        => Assert.Equal(expected, ReleaseTitleMatch.Mismatch(name, new ReleaseTitleExpectation("Pioneer One", 2010, IsTv: true, Season: 1)));

    /// <summary>
    /// #861: a release filed under a region-specific alternate title has
    /// nothing to match against unless the search also checks the metadata
    /// provider's other spellings for the title, not only Title/OriginalTitle.
    /// The alternate spelling here is deliberately unlike the primary title,
    /// so the release could only ever match through <c>OtherTitles</c>.
    /// </summary>
    [Fact]
    public void A_release_named_only_by_an_alternate_title_still_matches()
    {
        var withoutAlternates = new ReleaseTitleExpectation("The Wandering Earth II", 2023, IsTv: false);
        var withAlternates = withoutAlternates with { OtherTitles = ["Liu Lang Di Qiu 2"] };
        const string releaseName = "Liu.Lang.Di.Qiu.2.2023.1080p.WEB-DL";

        Assert.StartsWith("This release is for something else", ReleaseTitleMatch.Mismatch(releaseName, withoutAlternates));
        Assert.Null(ReleaseTitleMatch.Mismatch(releaseName, withAlternates));
    }

    [Fact]
    public void An_anime_named_by_its_absolute_number_is_checked_on_it()
    {
        var wanted = new ReleaseTitleExpectation("Frieren", null, IsTv: true, AbsoluteNumber: 12);
        Assert.Null(ReleaseTitleMatch.Mismatch("[SubsPlease] Frieren - 12 (1080p) [ABCD1234].mkv", wanted));
        Assert.Equal("This release is episode 13; the search was for episode 12.", ReleaseTitleMatch.Mismatch("[SubsPlease] Frieren - 13 (1080p).mkv", wanted));
    }

    /// <summary>#617: the name a download arrives under, read as a title and a year.</summary>
    [Theory]
    [InlineData("The Kid 1921 CC 1080p BluRay x264-nikt0", "The Kid", 1921)]
    [InlineData("house.on.-haunted.-hill.-1959.1080p.-blu-ray.-x-264-amiable", "house on haunted hill", 1959)]
    [InlineData("Sherlock.Jr.1924.BluRay.1080p", "Sherlock Jr", 1924)]
    [InlineData("[SubsPlease] Frieren - 12 (1080p)", "Frieren", null)]
    [InlineData("Pioneer.One.S01E01.720p.x264-VODO", "Pioneer One", null)]
    public void A_download_name_gives_its_title_and_year(string name, string title, int? year)
    {
        var parsed = ReleaseTitleMatch.ParseTitleAndYear(name);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Fact]
    public void Another_known_title_counts()
        => Assert.Null(ReleaseTitleMatch.Mismatch(
            "Sousou no Frieren - 12 (1080p)",
            new ReleaseTitleExpectation("Frieren: Beyond Journey's End", null, IsTv: true, AbsoluteNumber: 12, OtherTitles: ["Sousou no Frieren"])));
}
