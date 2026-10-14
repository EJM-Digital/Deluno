using Deluno.Contracts;
using Deluno.Tests.Support;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static Deluno.Tests.Support.ReleaseNameGenerators;

namespace Deluno.Platform.Tests.Properties;

/// <summary>
/// #1271: the episode and season parser (SxxEyy, multi-episode, daily, anime absolute, season packs) and
/// title matching, as properties. The name of each test states the invariant.
/// </summary>
public sealed class EpisodeAndTitleMatchingProperties
{
    private sealed record Show(string[] Words, string Separator, int Season, int Episode, int Extra, string Resolution)
    {
        public string Title => string.Join(' ', Words);

        public string Prefix => string.Join(Separator, Words);

        public string Tail => $"{Separator}{Resolution}{Separator}WEB-DL{Separator}x264-GRP";
    }

    private static Gen<Show> ShowGen()
        => from words in Words()
           from separator in Gen.Elements(".", " ", "-")
           from season in Gen.Choose(1, 40)
           from episode in Gen.Choose(1, 90)
           from extra in Gen.Choose(1, 5)
           from resolution in Resolution()
           select new Show(words, separator, season, episode, extra, resolution);

    private static Arbitrary<Show> Shows() => ShowGen().ToArbitrary();

    private static Arbitrary<(Show Show, DateOnly Date, int Offset)> DailyShows()
        => (from show in ShowGen()
            from days in Gen.Choose(0, 9000)
            from offset in Gen.Choose(1, 400)
            select (show, new DateOnly(2000, 1, 1).AddDays(days), offset)).ToArbitrary();

    private static ReleaseTitleExpectation Expect(Show show, int? season = null, int? episode = null)
        => new(show.Title, null, IsTv: true, season, episode);

    [Fact]
    public void A_name_joined_with_underscores_reads_its_episode_like_any_other_separator()
        => Prop.ForAll(
            (from words in Words() from season in Gen.Choose(1, 40) from episode in Gen.Choose(1, 90) select (words, season, episode)).ToArbitrary(),
            show =>
            {
                var title = string.Join(' ', show.words);
                var name = $"{string.Join('_', show.words)}_S{show.season:D2}E{show.episode:D2}_720p_WEB-DL_x264-GRP";
                return ReleaseTitleMatch.Mismatch(name, new ReleaseTitleExpectation(title, null, IsTv: true, show.season, show.episode)) is null
                       && ReleaseTitleMatch.Mismatch(name, new ReleaseTitleExpectation(title, null, IsTv: true, show.season, show.episode + 1)) is not null;
            }).QuickCheckThrowOnFailure();

    [Fact]
    public void An_underscore_joined_name_for_another_episode_is_refused_shrunk_case()
        => Assert.NotNull(ReleaseTitleMatch.Mismatch(
            "Paper_S22E60_480p_WEB-DL_x264-GRP",
            new ReleaseTitleExpectation("Paper", null, IsTv: true, 22, 61)));

    [Fact]
    public void SxxEyy_is_read_back_as_the_same_season_and_episode_and_the_same_title()
        => Prop.ForAll(Shows(), show =>
        {
            var parsed = FeedReleaseNames.Parse($"{show.Prefix}{show.Separator}S{show.Season:D2}E{show.Episode:D2}{show.Tail}");
            return parsed is { IsEpisode: true } p
                   && p.Season == show.Season && p.Episode == show.Episode
                   && FeedReleaseNames.NormalizeTitle(p.Title) == FeedReleaseNames.NormalizeTitle(show.Title);
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void SxxEyy_reads_the_same_in_any_case_and_with_a_one_digit_season()
        => Prop.ForAll(Shows(), show =>
        {
            var upper = FeedReleaseNames.Parse($"{show.Prefix}.S{show.Season}E{show.Episode:D2}{show.Tail}");
            var lower = FeedReleaseNames.Parse($"{show.Prefix}.s{show.Season}e{show.Episode:D2}{show.Tail}".ToLowerInvariant());
            return upper is { } && lower is { } && upper.Season == lower.Season && upper.Episode == lower.Episode;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_name_for_the_season_and_episode_asked_for_is_accepted()
        => Prop.ForAll(Shows(), show =>
            ReleaseTitleMatch.Mismatch($"{show.Prefix}{show.Separator}S{show.Season:D2}E{show.Episode:D2}{show.Tail}", Expect(show, show.Season, show.Episode)) is null)
            .QuickCheckThrowOnFailure();

    [Fact]
    public void A_name_for_another_episode_of_the_same_season_is_refused()
        => Prop.ForAll(Shows(), show =>
            ReleaseTitleMatch.Mismatch($"{show.Prefix}{show.Separator}S{show.Season:D2}E{show.Episode + show.Extra:D2}{show.Tail}", Expect(show, show.Season, show.Episode)) is not null)
            .QuickCheckThrowOnFailure();

    [Fact]
    public void A_name_for_another_season_is_refused()
        => Prop.ForAll(Shows(), show =>
            ReleaseTitleMatch.Mismatch($"{show.Prefix}{show.Separator}S{show.Season + show.Extra:D2}E{show.Episode:D2}{show.Tail}", Expect(show, show.Season, show.Episode)) is not null)
            .QuickCheckThrowOnFailure();

    [Fact]
    public void A_multi_episode_name_is_accepted_for_exactly_the_episodes_it_spans_whichever_way_the_range_is_written()
        => Prop.ForAll(Shows(), show =>
        {
            var last = show.Episode + show.Extra;
            string[] spellings =
            [
                $"S{show.Season:D2}E{show.Episode:D2}-E{last:D2}",
                $"S{show.Season:D2}E{show.Episode:D2}-{last:D2}"
            ];
            return spellings.All(spelling =>
            {
                var name = $"{show.Prefix}{show.Separator}{spelling}{show.Tail}";
                var inside = Enumerable.Range(show.Episode, show.Extra + 1)
                    .All(episode => ReleaseTitleMatch.Mismatch(name, Expect(show, show.Season, episode)) is null);
                var before = ReleaseTitleMatch.Mismatch(name, Expect(show, show.Season, show.Episode - 1)) is not null;
                var after = ReleaseTitleMatch.Mismatch(name, Expect(show, show.Season, last + 1)) is not null;
                return inside && before && after;
            });
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_multi_episode_name_written_S01E01E02_is_accepted_for_each_episode_it_holds()
        => Prop.ForAll(Shows(), show =>
        {
            var name = $"{show.Prefix}{show.Separator}S{show.Season:D2}E{show.Episode:D2}E{show.Episode + show.Extra:D2}{show.Tail}";
            return Enumerable.Range(show.Episode, show.Extra + 1)
                .All(episode => ReleaseTitleMatch.Mismatch(name, Expect(show, show.Season, episode)) is null);
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void Blade_S16E65E68_is_for_Blade_episode_65_shrunk_case()
        => Assert.Null(ReleaseTitleMatch.Mismatch(
            "Blade.S16E65E68.576p.WEB-DL.x264-GRP",
            new ReleaseTitleExpectation("Blade", null, IsTv: true, 16, 65)));

    [Fact]
    public void The_NxNN_form_means_the_same_as_SxxEyy()
        => Prop.ForAll(Shows(), show =>
            ReleaseTitleMatch.Mismatch($"{show.Prefix}{show.Separator}{show.Season}x{show.Episode:D2}{show.Tail}", Expect(show, show.Season, show.Episode)) is null
            && ReleaseTitleMatch.Mismatch($"{show.Prefix}{show.Separator}{show.Season}x{show.Episode + show.Extra:D2}{show.Tail}", Expect(show, show.Season, show.Episode)) is not null)
            .QuickCheckThrowOnFailure();

    [Fact]
    public void A_season_pack_is_accepted_for_its_season_and_refused_for_another_season_and_for_one_episode()
        => Prop.ForAll(Shows(), show =>
        {
            var name = $"{show.Prefix}{show.Separator}S{show.Season:D2}{show.Tail}";
            return ReleaseTitleMatch.Mismatch(name, Expect(show, show.Season)) is null
                   && ReleaseTitleMatch.Mismatch(name, Expect(show, show.Season + show.Extra)) is not null
                   && ReleaseTitleMatch.Mismatch(name, Expect(show, show.Season, show.Episode)) is not null;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void An_anime_name_with_an_absolute_number_is_accepted_for_that_number_and_refused_for_another()
        => Prop.ForAll(Shows(), show =>
        {
            var absolute = (show.Season * 10) + show.Episode;
            var name = $"{show.Title} - {absolute} [{show.Resolution}]";
            var expected = new ReleaseTitleExpectation(show.Title, null, IsTv: true, AbsoluteNumber: absolute);
            var wrong = expected with { AbsoluteNumber = absolute + show.Extra };
            return ReleaseTitleMatch.Mismatch(name, expected) is null && ReleaseTitleMatch.Mismatch(name, wrong) is not null;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_daily_name_is_accepted_for_the_date_it_carries()
        => Prop.ForAll(DailyShows(), item =>
        {
            var name = $"{item.Show.Prefix}{item.Show.Separator}{item.Date:yyyy.MM.dd}{item.Show.Tail}";
            return ReleaseTitleMatch.Mismatch(name, new ReleaseTitleExpectation(item.Show.Title, null, IsTv: true, AirDate: item.Date)) is null;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_daily_name_for_another_date_is_refused()
        => Prop.ForAll(DailyShows(), item =>
        {
            var name = $"{item.Show.Prefix}{item.Show.Separator}{item.Date:yyyy.MM.dd}{item.Show.Tail}";
            var other = item.Date.AddDays(item.Offset);
            return ReleaseTitleMatch.Mismatch(name, new ReleaseTitleExpectation(item.Show.Title, null, IsTv: true, AirDate: other)) is not null;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_daily_name_for_the_next_day_is_refused_shrunk_case()
        => Assert.NotNull(ReleaseTitleMatch.Mismatch(
            "Night.Living.2000.01.01.720p.WEB-DL.x264-GRP",
            new ReleaseTitleExpectation("Night Living", null, IsTv: true, AirDate: new DateOnly(2000, 1, 2))));

    [Fact]
    public void A_film_name_built_from_the_title_and_year_is_accepted_with_any_separator_and_case_and_the_wrong_year_is_refused()
        => Prop.ForAll(
            (from words in Words()
             from sep in Separator()
             from year in Gen.Choose(1925, 2025)
             from upper in Gen.Elements(true, false)
             select (words, sep, year, upper)).ToArbitrary(),
            film =>
            {
                var title = string.Join(' ', film.words);
                var name = $"{string.Join(film.sep, film.words)}{film.sep}{film.year}{film.sep}1080p{film.sep}BluRay{film.sep}x264-GRP";
                if (film.upper)
                {
                    name = name.ToUpperInvariant();
                }

                var expected = new ReleaseTitleExpectation(title, film.year, IsTv: false);
                return ReleaseTitleMatch.Mismatch(name, expected) is null
                       && ReleaseTitleMatch.Mismatch(name, expected with { Year = film.year + 5 }) is not null;
            }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_name_that_gives_a_different_title_is_refused()
        => Prop.ForAll(
            (from a in Words(1, 2)
             from b in Words(1, 2)
             where !a.Intersect(b).Any()
             select (a, b)).ToArbitrary(),
            pair =>
            {
                var name = $"{string.Join('.', pair.a)}.2001.1080p.BluRay.x264-GRP";
                return ReleaseTitleMatch.Mismatch(name, new ReleaseTitleExpectation(string.Join(' ', pair.b), 2001, IsTv: false)) is not null;
            }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_name_that_also_lists_the_wanted_title_after_a_slash_is_accepted()
        => Prop.ForAll(Shows(), show =>
            ReleaseTitleMatch.Mismatch($"Other Words / {show.Title} (2001) 1080p BluRay", new ReleaseTitleExpectation(show.Title, 2001, IsTv: false)) is null)
            .QuickCheckThrowOnFailure();

    [Property]
    public bool Clean_is_case_blind_and_punctuation_blind(NonEmptyString text)
        => ReleaseTitleMatch.Clean(text.Get) == ReleaseTitleMatch.Clean(text.Get.ToUpperInvariant())
           && ReleaseTitleMatch.Clean(text.Get) == ReleaseTitleMatch.Clean(text.Get.Replace(' ', '.'))
           && ReleaseTitleMatch.Clean(text.Get).All(char.IsLetterOrDigit);

    [Property]
    public bool Mismatch_never_throws_for_any_name_and_any_search(string? name, NonEmptyString title, bool isTv, int season, int episode)
    {
        _ = ReleaseTitleMatch.Mismatch(name ?? string.Empty, new ReleaseTitleExpectation(title.Get, 2001, isTv, season, episode));
        return true;
    }

    [Property]
    public bool NormalizeTitle_is_idempotent_lower_case_and_single_spaced(string? text)
    {
        var once = FeedReleaseNames.NormalizeTitle(text);
        return FeedReleaseNames.NormalizeTitle(once) == once
               && once == once.ToLowerInvariant()
               && !once.Contains("  ", StringComparison.Ordinal)
               && once == once.Trim();
    }

    [Property]
    public bool FeedReleaseNames_never_throws_and_a_season_and_episode_it_reads_are_in_range(string? text)
    {
        var parsed = FeedReleaseNames.Parse(text);
        return parsed is null
               || (parsed.Season is null or >= 0 and <= 99
                   && parsed.Episode is null or >= 0 and <= 999
                   && parsed.Year is null or >= 1900 and <= 2199);
    }
}
