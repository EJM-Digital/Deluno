using System.Runtime.CompilerServices;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Retention;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Moq;

namespace Deluno.Worker.Tests.Retention;

/// <summary>
/// The retention rule fields #1003 to #1007 and #1014. Each one is checked for
/// what it answers, and for the rule that matters most: an answer Deluno cannot
/// give is "cannot say" (null), which never selects a title.
/// </summary>
public sealed class RetentionRuleFieldTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
    private static readonly MediaServerConnection Server = new("server-1", "plex", "Rig lab Plex", "http://127.0.0.1:32400", "token", true, "healthy", null, null, null, "WIN-RIG", "id", Now, Now);
    private const string FilePath = "/deluno/movies/Nosferatu (1922)/Nosferatu (1922).mkv";

    // ------------------------------------------------------------- #1003 audio
    [Theory]
    [InlineData("in", "French", true)]
    [InlineData("in", "fr", true)]
    [InlineData("in", "fre", true)]
    [InlineData("in", "German", false)]
    [InlineData("notin", "German", true)]
    [InlineData("all", "English|French", true)]
    public void Audio_languages_read_a_typed_language_the_way_the_servers_tags_are(string op, string typed, bool expected)
    {
        var answer = Ask(Movie(), "server.audioLanguages", op, typed, Item() with { AudioLanguages = ["English", "French"] });

        Assert.Equal(expected, answer.Holds);
    }

    [Theory]
    [InlineData("in")]
    [InlineData("notin")]
    [InlineData("unset")]
    [InlineData("set")]
    public void Audio_languages_the_server_did_not_say_never_match_any_test(string op)
    {
        var answer = Ask(Movie(), "server.audioLanguages", op, "French", Item());

        Assert.Null(answer.Holds);
    }

    [Fact]
    public void Audio_languages_of_a_file_with_an_untagged_track_are_not_known()
    {
        Assert.Null(MediaServerLanguages.OfAudioTracks(["eng", null]));
        Assert.Null(MediaServerLanguages.OfAudioTracks(["eng", "und"]));
        Assert.Null(MediaServerLanguages.OfAudioTracks([]));
        Assert.Equal(["English", "Japanese"], MediaServerLanguages.OfAudioTracks(["eng", "jpn", "en"]));
    }

    [Theory]
    [InlineData("tgl", "Tagalog")]
    [InlineData("fil", "Tagalog")]
    [InlineData("fas", "Persian")]
    [InlineData("cat", "Catalan")]
    public void Languages_beyond_the_common_few_are_named_from_a_full_enough_table(string tag, string name)
    {
        Assert.Equal(name, MediaServerLanguages.Name(tag));
        Assert.Equal(name, MediaServerLanguages.Name(name));
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("tlh")]
    [InlineData("unknown")]
    [InlineData("unk")]
    [InlineData("undefined")]
    [InlineData("mis")]
    [InlineData("qaa")]
    [InlineData("qtz")]
    [InlineData("QAB")]
    [InlineData("na")]
    [InlineData("Not applicable")]
    [InlineData("zxx")]
    [InlineData("und")]
    public void A_tag_that_names_no_known_language_is_not_a_language(string tag)
    {
        Assert.Null(MediaServerLanguages.Name(tag));
        // One such track makes the whole file's list unknown, never a shorter list.
        Assert.Null(MediaServerLanguages.OfAudioTracks(["eng", tag]));
    }

    [Fact]
    public void A_does_not_include_delete_rule_never_matches_a_file_whose_languages_are_uncertain()
    {
        // Audio tagged English and "tgl" while the rule says "does not include Tagalog": the tag is
        // known now, but an unnamed tag must have left the list unknown, so nothing is decided.
        Assert.Null(Ask(Movie(), "server.audioLanguages", "notin", "French", Item() with { AudioLanguages = MediaServerLanguages.OfAudioTracks(["eng", "xyz"]) }).Holds);
        Assert.Null(Ask(Movie(), "server.audioLanguages", "notin", "French", Item() with { AudioLanguages = MediaServerLanguages.OfAudioTracks(["eng", "unknown"]) }).Holds);
        Assert.True(Ask(Movie(), "server.audioLanguages", "notin", "French", Item() with { AudioLanguages = MediaServerLanguages.OfAudioTracks(["eng", "jpn"]) }).Holds);
    }

    [Fact]
    public void A_language_typed_in_a_rule_that_deluno_does_not_know_is_undecided_and_refused_on_save()
    {
        Assert.Null(Ask(Movie(), "server.audioLanguages", "notin", "Klingon", Item() with { AudioLanguages = ["English"] }).Holds);

        var rule = new SaveRetentionRuleRequest("Rule", RetentionLevels.Movie, true, [RetentionStep.KeepOnly("server.audioLanguages:notin:Klingon")], ["server-1"], false);
        Assert.Contains(RetentionEvaluator.Validate(rule, [Server]), problem => problem.Contains("not a language Deluno knows", StringComparison.Ordinal));
    }

    [Fact]
    public void Viewed_through_season_is_undecided_when_the_server_lists_no_episode_that_far()
    {
        var (show, _) = ThreeSeasons(Now.AddDays(-60), Now.AddDays(-30), Now.AddDays(-1));

        Assert.Null(Ask(SeasonUnit(2), "server.viewedThroughSeason", "unset", null, [show, ServerEpisode("only3", 3, 1, [])]).Holds);
    }

    // -------------------------------------------------- #1006 one named person
    [Fact]
    public void One_persons_views_count_only_their_own_finished_plays()
    {
        var item = Item() with
        {
            Viewers =
            [
                new MediaServerViewer("owner", true, 3, Now.AddDays(-5), false, null),
                new MediaServerViewer("ben", true, 9, Now.AddDays(-2), false, null)
            ]
        };

        Assert.True(Ask(Movie(), "server.personViews", "min", "3", item, person: "owner").Holds);
        Assert.False(Ask(Movie(), "server.personViews", "min", "4", item, person: "Owner").Holds);
        Assert.True(Ask(Movie(), "server.personViews", "is", "0", Item(), person: "ben").Holds);
    }

    [Fact]
    public void One_persons_last_view_is_theirs_not_everyones()
    {
        var item = Item() with
        {
            Viewers =
            [
                new MediaServerViewer("owner", true, 1, Now.AddDays(-40), false, null),
                new MediaServerViewer("ben", true, 1, Now.AddDays(-2), false, null)
            ]
        };

        Assert.True(Ask(Movie(), "server.personLastViewed", "beyond", "30", item, person: "owner").Holds);
        Assert.False(Ask(Movie(), "server.personLastViewed", "beyond", "30", item, person: "ben").Holds);
    }

    [Fact]
    public void A_person_the_server_does_not_list_is_undecided_never_zero_views()
    {
        foreach (var field in new[] { "server.personViews", "server.personWatchMinutes", "server.personLastViewed" })
        {
            var answer = Ask(Movie(), field, field.EndsWith("LastViewed", StringComparison.Ordinal) ? "unset" : "is", "0", Item(), person: "stranger");
            Assert.Null(answer.Holds);
        }
    }

    [Fact]
    public void One_persons_watch_minutes_are_finished_plays_times_the_running_time()
    {
        var item = Item() with
        {
            Runtime = TimeSpan.FromMinutes(90),
            Viewers = [new MediaServerViewer("owner", true, 2, Now.AddDays(-5), false, null)]
        };

        var answer = Ask(Movie(), "server.personWatchMinutes", "min", "180", item, person: "owner");

        Assert.True(answer.Holds);
    }

    [Fact]
    public void Watch_minutes_without_a_running_time_are_undecided()
    {
        var item = Item() with { Viewers = [new MediaServerViewer("owner", true, 2, Now.AddDays(-5), false, null)] };

        Assert.Null(Ask(Movie(), "server.personWatchMinutes", "min", "1", item, person: "owner").Holds);
    }

    [Fact]
    public void A_show_counts_one_persons_views_across_its_episodes()
    {
        var (show, episodes) = ShowWithEpisodes(
            [new MediaServerViewer("owner", true, 2, Now.AddDays(-9), false, null)],
            [new MediaServerViewer("owner", true, 1, Now.AddDays(-3), false, null), new MediaServerViewer("ben", true, 5, Now.AddDays(-3), false, null)]);

        var answer = Ask(ShowUnit(), "server.personViews", "is", "3", [show, .. episodes], person: "owner");

        Assert.True(answer.Holds);
    }

    [Fact]
    public void A_per_person_condition_names_whose_and_a_plain_one_cannot()
    {
        var rule = new SaveRetentionRuleRequest("Rule", RetentionLevels.Movie, true, [RetentionStep.KeepOnly("server.personViews:min:1")], ["server-1"], false);
        var named = rule with { Steps = [RetentionStep.KeepOnly("server.personViews@the owner%20Smith:min:1")] };
        var plainWithPerson = rule with { Steps = [RetentionStep.KeepOnly("server.timesViewed@owner:min:1")] };

        Assert.Contains(RetentionEvaluator.Validate(rule, [Server]), problem => problem.Contains("needs the person", StringComparison.Ordinal));
        Assert.Empty(RetentionEvaluator.Validate(named, [Server]));
        Assert.Contains(RetentionEvaluator.Validate(plainWithPerson, [Server]), problem => problem.Contains("not asked about one person", StringComparison.Ordinal));
        Assert.Equal("James Smith", RetentionFieldCatalog.PersonOf("server.personViews@the owner%20Smith"));
    }

    // ---------------------------------------------------------- #1007 collections
    [Fact]
    public void Collection_newest_view_is_the_newest_view_of_anything_sharing_a_collection()
    {
        var viewed = Item("j2") with
        {
            Collections = ["Horror classics"],
            Viewers = [new MediaServerViewer("owner", true, 1, Now.AddDays(-3), false, null)]
        };
        var mine = Item() with { Collections = ["Horror classics"], Viewers = [new MediaServerViewer("owner", true, 1, Now.AddDays(-90), false, null)] };
        var other = Item("j3") with { Collections = ["Other"], Viewers = [new MediaServerViewer("owner", true, 1, Now.AddDays(-1), false, null)] };

        var recent = Ask(Movie(), "server.collectionNewestView", "within", "7", mine, viewed, other);
        var ownOnly = Ask(Movie(), "server.collectionNewestView", "beyond", "30", mine, other);

        Assert.True(recent.Holds);
        Assert.True(ownOnly.Holds);
    }

    [Fact]
    public void Collection_newest_view_is_undecided_when_it_is_in_no_collection()
    {
        foreach (var op in new[] { "beyond", "unset", "within" })
        {
            Assert.Null(Ask(Movie(), "server.collectionNewestView", op, "30", Item()).Holds);
        }
    }

    [Fact]
    public void Viewed_through_season_looks_at_this_season_and_every_earlier_one_only()
    {
        var (show, episodes) = ThreeSeasons(
            season1: Now.AddDays(-60),
            season2: Now.AddDays(-30),
            season3: Now.AddDays(-1));
        var seasonTwo = SeasonUnit(2);

        // Season 3 was watched yesterday, but that is ahead of season 2: not counted.
        var answer = Ask(seasonTwo, "server.viewedThroughSeason", "beyond", "20", [show, .. episodes]);
        var throughThree = Ask(SeasonUnit(3), "server.viewedThroughSeason", "within", "7", [show, .. episodes]);

        Assert.True(answer.Holds);
        Assert.True(throughThree.Holds);
    }

    [Fact]
    public void Viewed_through_season_keeps_specials_apart_from_the_numbered_seasons()
    {
        var (show, episodes) = ThreeSeasons(Now.AddDays(-60), Now.AddDays(-30), Now.AddDays(-1));

        // Nothing in specials has been viewed: "never", not the seasons' dates.
        var answer = Ask(SeasonUnit(0), "server.viewedThroughSeason", "unset", null, [show, .. episodes]);

        Assert.True(answer.Holds);
    }

    [Fact]
    public void Smart_collections_are_counted_with_ordinary_ones_and_unknown_when_not_read()
    {
        var known = Item() with { Collections = ["Horror classics"], SmartCollections = ["Silent films", "Horror classics"] };
        var unknown = Item() with { Collections = ["Horror classics"], SmartCollections = null };

        Assert.True(Ask(Movie(), "server.collectionsWithSmart", "is", "2", known).Holds);
        Assert.Null(Ask(Movie(), "server.collectionsWithSmart", "min", "0", unknown).Holds);
    }

    [Fact]
    public void Smart_collections_are_refused_where_no_chosen_server_has_them()
    {
        var jellyfin = Server with { Kind = "jellyfin" };
        var rule = new SaveRetentionRuleRequest("Rule", RetentionLevels.Movie, true, [RetentionStep.KeepOnly("server.collectionsWithSmart:min:1")], ["server-1"], false);

        Assert.Contains(RetentionEvaluator.Validate(rule, [jellyfin]), problem => problem.Contains("None of the chosen servers", StringComparison.Ordinal));
    }

    // ------------------------------------------- #1014 profile and #1005 series
    [Fact]
    public void Quality_profile_and_monitored_answer_from_what_deluno_holds_and_say_so_when_they_do_not()
    {
        var known = Movie() with { Facts = new RetentionTitleFacts(QualityProfile: "HD-1080p", Monitored: true) };
        var unknown = Movie() with { Facts = new RetentionTitleFacts() };

        Assert.True(Ask(known, "title.qualityProfile", "is", "hd-1080p").Holds);
        Assert.True(Ask(known, "title.monitored", "is", "true").Holds);
        Assert.False(Ask(known, "title.monitored", "is", "false").Holds);
        foreach (var (field, op, value) in new[] { ("title.qualityProfile", "isnot", "HD-1080p"), ("title.qualityProfile", "unset", ""), ("title.monitored", "is", "false") })
        {
            Assert.Null(Ask(unknown, field, op, value).Holds);
        }

        Assert.Null(Ask(Movie(), "title.monitored", "is", "false").Holds);
    }

    [Fact]
    public async Task The_reader_gives_a_movie_its_own_profile_over_its_librarys_and_a_shows_monitoring()
    {
        var (reader, _) = Reader(movieOverride: "p-remux", showOverride: null);

        var movies = await reader.ReadAsync(RetentionLevels.Movie, CancellationToken.None, RetentionUnitNeeds.Profile);
        var shows = await reader.ReadAsync(RetentionLevels.Show, CancellationToken.None, RetentionUnitNeeds.Profile);
        var plain = await reader.ReadAsync(RetentionLevels.Movie, CancellationToken.None);

        Assert.Equal("Remux", movies.Single().Facts!.QualityProfile);
        Assert.True(movies.Single().Facts!.Monitored);
        Assert.Equal("Standard", shows.Single().Facts!.QualityProfile);
        Assert.Equal("anime", shows.Single().Facts!.SeriesType);
        Assert.False(shows.Single().Facts!.Monitored);
        // A rule that does not read the profile asks nothing extra.
        Assert.Null(plain.Single().Facts!.QualityProfile);
    }

    [Fact]
    public void A_show_type_is_answered_at_every_level_and_offers_its_choices()
    {
        var unit = ShowUnit() with { Facts = new RetentionTitleFacts(SeriesType: "anime") };
        var field = RetentionFieldCatalog.Find("title.seriesType")!;

        Assert.True(Ask(unit, "title.seriesType", "is", "anime").Holds);
        Assert.False(Ask(unit, "title.seriesType", "is", "daily").Holds);
        Assert.Equal(["standard", "daily", "anime"], field.Options);
    }

    // ------------------------------------------------ #1004 and #1005 episodes
    private static SeriesEpisodeInventoryItem Episode(string id, int season, int number, DateTimeOffset? aired, bool hasFile, bool monitored = true)
        => new(id, season, number, null, null, aired, monitored, hasFile, "", "", false, null, null, false, null, null, null, Now, null, null, null, null, null);

    [Fact]
    public void Episodes_rank_newest_first_among_aired_files_and_specials_and_unaired_are_left_out()
    {
        var math = new RetentionEpisodeMath(
        [
            Episode("s0", 0, 1, Now.AddDays(-1), true),
            Episode("a", 1, 1, Now.AddDays(-30), true),
            Episode("b", 1, 2, Now.AddDays(-23), true),
            Episode("c", 2, 1, Now.AddDays(-9), true),
            Episode("d", 2, 2, Now.AddDays(-2), true),
            Episode("gone", 2, 3, Now.AddDays(-1), false),
            Episode("future", 2, 4, Now.AddDays(5), false),
            Episode("undated", 2, 5, null, true)
        ], Now);

        Assert.Equal(1, math.EpisodeRank("d"));
        Assert.Equal(2, math.EpisodeRank("c"));
        Assert.Equal(4, math.EpisodeRank("a"));
        Assert.Null(math.EpisodeRank("s0"));
        Assert.Null(math.EpisodeRank("gone"));
        Assert.Null(math.EpisodeRank("future"));
        Assert.Null(math.EpisodeRank("undated"));
        Assert.Equal(1, math.SeasonRank(2));
        Assert.Equal(2, math.SeasonRank(1));
        Assert.Null(math.SeasonRank(0));
    }

    [Fact]
    public void An_episode_out_of_the_rank_pool_is_undecided_so_keep_the_newest_5_never_removes_it()
    {
        var unit = EpisodeUnit() with { Facts = new RetentionTitleFacts(EpisodeRank: null) };

        Assert.Null(Ask(unit, "title.episodeRank", "min", "6").Holds);
        Assert.True(Ask(EpisodeUnit() with { Facts = new RetentionTitleFacts(EpisodeRank: 6) }, "title.episodeRank", "min", "6").Holds);
        Assert.False(Ask(EpisodeUnit() with { Facts = new RetentionTitleFacts(EpisodeRank: 5) }, "title.episodeRank", "min", "6").Holds);
    }

    [Fact]
    public void Missing_episodes_are_monitored_aired_episodes_with_no_file()
    {
        var math = new RetentionEpisodeMath(
        [
            Episode("a", 1, 1, Now.AddDays(-30), true),
            Episode("b", 1, 2, Now.AddDays(-23), false),
            Episode("c", 1, 3, Now.AddDays(-22), false, monitored: false),
            Episode("d", 1, 4, Now.AddDays(4), false),
            Episode("e", 2, 1, Now.AddDays(-2), false),
            Episode("s", 0, 1, Now.AddDays(-2), false)
        ], Now);

        Assert.Equal(2, math.Missing(null));
        Assert.Equal(1, math.Missing(1));
        Assert.Equal(1, math.Missing(0));
    }

    [Fact]
    public void Missing_episodes_are_unknown_while_a_monitored_episode_without_a_file_has_no_air_date()
    {
        var math = new RetentionEpisodeMath(
        [
            Episode("a", 1, 1, Now.AddDays(-30), true),
            Episode("b", 1, 2, null, false),
            Episode("c", 2, 1, Now.AddDays(-30), true),
            // Not monitored, so it does not count either way.
            Episode("d", 2, 2, null, false, monitored: false),
            Episode("e", 3, 1, Now.AddDays(-2), false)
        ], Now);

        // Undated is Upcoming, not missing (#1072): only aired episodes count.
        Assert.Equal(1, math.Missing(null));
        Assert.Equal(0, math.Missing(1));
        Assert.Equal(0, math.Missing(2));
        Assert.Equal(1, math.Missing(3));
    }

    [Fact]
    public void Upcoming_means_not_aired_or_no_air_date_and_a_season_or_show_is_upcoming_only_when_none_of_it_has_aired()
    {
        var future = Episode("f", 1, 1, Now.AddDays(3), false);
        var undated = Episode("u", 1, 2, null, false);
        var aired = Episode("a", 2, 1, Now.AddDays(-3), true);

        Assert.True(RetentionEpisodeMath.EpisodeUpcoming(future, Now));
        Assert.True(RetentionEpisodeMath.EpisodeUpcoming(undated, Now));
        Assert.False(RetentionEpisodeMath.EpisodeUpcoming(aired, Now));

        var math = new RetentionEpisodeMath([future, undated, aired], Now);
        Assert.True(math.SeasonUpcoming(1));
        Assert.False(math.SeasonUpcoming(2));
        Assert.False(math.SeasonUpcoming(9));
        Assert.False(math.ShowUpcoming());
        Assert.True(new RetentionEpisodeMath([future, undated], Now).ShowUpcoming());
        Assert.False(new RetentionEpisodeMath([], Now).ShowUpcoming());
    }

    [Fact]
    public void A_season_is_monitored_while_any_of_its_episodes_is()
    {
        var math = new RetentionEpisodeMath([Episode("a", 1, 1, Now.AddDays(-9), true, monitored: false), Episode("b", 1, 2, Now.AddDays(-8), true, monitored: true), Episode("c", 2, 1, Now.AddDays(-2), true, monitored: false)], Now);

        Assert.True(math.SeasonMonitored(1));
        Assert.False(math.SeasonMonitored(2));
        Assert.Null(math.SeasonMonitored(7));
    }

    [Fact]
    public void A_season_finale_is_the_last_episode_of_a_season_with_nothing_still_to_air()
    {
        var math = new RetentionEpisodeMath(
        [
            Episode("a", 1, 1, Now.AddDays(-30), true),
            Episode("b", 1, 2, Now.AddDays(-23), true),
            Episode("c", 2, 1, Now.AddDays(-9), true),
            Episode("d", 2, 2, Now.AddDays(-2), true),
            Episode("e", 2, 3, Now.AddDays(5), false)
        ], Now);

        Assert.True(math.SeasonFinale(1, null));
        Assert.True(math.SeasonFinale(1, "b"));
        Assert.False(math.SeasonFinale(1, "a"));
        // Season 2 still has an episode to air, so its last aired one is not the finale, and the real last one is not on disk.
        Assert.False(math.SeasonFinale(2, "d"));
        Assert.False(math.SeasonFinale(2, "e"));
        Assert.False(math.SeasonFinale(2, null));
        Assert.False(math.SeasonFinale(0, null));

        // The last episode is on disk while an undated one is listed before it: not known to be over.
        var undated = new RetentionEpisodeMath([Episode("x", 1, 1, null, false), Episode("y", 1, 2, Now.AddDays(-2), true)], Now);
        Assert.Null(undated.SeasonFinale(1, "y"));
    }

    [Fact]
    public void A_season_whose_last_episode_is_not_on_disk_has_no_finale_held()
    {
        var math = new RetentionEpisodeMath([Episode("a", 1, 1, Now.AddDays(-30), true), Episode("b", 1, 2, Now.AddDays(-23), false)], Now);

        Assert.False(math.SeasonFinale(1, null));
    }

    [Fact]
    public void A_series_finale_needs_the_show_to_have_ended_and_is_undecided_when_that_is_not_known()
    {
        var math = new RetentionEpisodeMath(
        [
            Episode("a", 1, 1, Now.AddDays(-30), true),
            Episode("b", 2, 1, Now.AddDays(-9), true),
            Episode("c", 2, 2, Now.AddDays(-2), true)
        ], Now);

        Assert.True(math.SeriesFinale(null, null, ended: true));
        Assert.True(math.SeriesFinale(2, "c", ended: true));
        Assert.False(math.SeriesFinale(2, "b", ended: true));
        Assert.False(math.SeriesFinale(1, null, ended: true));
        // Still running: no episode is the series finale.
        Assert.False(math.SeriesFinale(null, null, ended: false));
        // Status not known: cannot say.
        Assert.Null(math.SeriesFinale(null, null, ended: null));
        Assert.Null(math.SeriesFinale(2, "c", ended: null));
    }

    [Fact]
    public void Finale_flags_that_are_not_known_never_match_either_way()
    {
        var unit = EpisodeUnit() with { Facts = new RetentionTitleFacts(SeasonFinale: null, SeriesFinale: null) };

        foreach (var field in new[] { "title.seasonFinale", "title.seriesFinale", "title.missingEpisodes", "title.seasonRank" })
        {
            Assert.Null(Ask(unit, field, field.EndsWith("Finale", StringComparison.Ordinal) ? "is" : "min", field.EndsWith("Finale", StringComparison.Ordinal) ? "false" : "0").Holds);
        }

        Assert.True(Ask(EpisodeUnit() with { Facts = new RetentionTitleFacts(SeasonFinale: true) }, "title.seasonFinale", "is", "true").Holds);
    }

    [Fact]
    public void The_new_fields_ask_for_what_they_need_and_a_rule_without_them_asks_for_nothing()
    {
        Assert.Equal(RetentionUnitNeeds.None, RetentionFieldCatalog.NeedsOf(["server.watchedBy:anyone:", "size:min:1"]));
        Assert.Equal(RetentionUnitNeeds.Inventory | RetentionUnitNeeds.ShowStatus, RetentionFieldCatalog.NeedsOf(["title.seriesFinale:is:true"]));
        Assert.Equal(RetentionUnitNeeds.Profile, RetentionFieldCatalog.NeedsOf(["title.qualityProfile:is:Remux"]));
    }

    // ------------------------------------------------------------------ helpers
    private static RetentionAnswer Ask(RetentionUnit unit, string fieldId, string op, string? value, params MediaServerItem[] items)
        => Ask(unit, fieldId, op, value, items, null);

    private static RetentionAnswer Ask(RetentionUnit unit, string fieldId, string op, string? value, MediaServerItem item, string? person)
        => Ask(unit, fieldId, op, value, [item], person);

    private static RetentionAnswer Ask(RetentionUnit unit, string fieldId, string op, string? value, MediaServerItem[] items, string? person)
        => new RetentionFieldAnswers([new RetentionServerIndex(Server, new MediaServerLibrary(items, ["owner", "ben"]))], new NullDisk(), Now)
            .Answer(unit, RetentionFieldCatalog.Find(fieldId)!, op, value, person);

    private static MediaServerItem Item(string id = "j1")
        => new(id, "movies", id == "j1" ? "Nosferatu" : $"Film {id}", 1922, id == "j1" ? "653" : null, id == "j1" ? "tt0013442" : null, null, [id == "j1" ? FilePath : $"/deluno/movies/{id}/{id}.mkv"], Now.AddDays(-100), MediaServerWatchState.Unplayed);

    private static RetentionUnit Movie()
        => new(RetentionLevels.Movie, "m1", null, null, null, "Nosferatu", "Nosferatu", 1922, "tt0013442", "653", null, 1024, [FilePath], []);

    private static RetentionUnit ShowUnit()
        => new(RetentionLevels.Show, "s1", null, null, null, "Land of the Dead", "Land of the Dead", 2005, null, "12345", null, 1024, ["/deluno/tv/Land of the Dead"], []);

    private static RetentionUnit SeasonUnit(int season)
        => new(RetentionLevels.Season, $"s1:s{season}", "s1", season, null, "Land of the Dead", "Land of the Dead", 2005, null, "12345", null, 1024, ["/deluno/tv/Land of the Dead/x.mkv"], [],
            new RetentionPartFacts(false, false, 1, null, null, null, null, null, null, []));

    private static RetentionUnit EpisodeUnit()
        => new(RetentionLevels.Episode, "e1", "s1", 1, 1, "Show — S01E01", "Show", 2020, null, null, null, 1024, [FilePath], [],
            new RetentionPartFacts(true, false, 1, null, null, null, null, null, FilePath, ["e1"]));

    private static MediaServerItem ServerShow()
        => new("show-1", "tv", "Land of the Dead", 2005, "12345", null, null, ["/deluno/tv/Land of the Dead"], Now.AddDays(-100), MediaServerWatchState.Unplayed) { Level = MediaServerItemLevels.Show };

    private static MediaServerItem ServerEpisode(string id, int season, int number, IReadOnlyList<MediaServerViewer> viewers)
        => new(id, "tv", $"S{season}E{number}", 2005, null, null, null, [], Now.AddDays(-100), MediaServerWatchState.Unplayed)
        {
            Level = MediaServerItemLevels.Episode,
            ShowServerItemId = "show-1",
            SeasonNumber = season,
            EpisodeNumber = number,
            Viewers = viewers
        };

    private static (MediaServerItem Show, MediaServerItem[] Episodes) ShowWithEpisodes(IReadOnlyList<MediaServerViewer> first, IReadOnlyList<MediaServerViewer> second)
        => (ServerShow(), [ServerEpisode("ep-1", 1, 1, first), ServerEpisode("ep-2", 1, 2, second)]);

    private static (MediaServerItem Show, MediaServerItem[] Episodes) ThreeSeasons(DateTimeOffset season1, DateTimeOffset season2, DateTimeOffset season3)
    {
        MediaServerViewer[] Viewed(DateTimeOffset when) => [new MediaServerViewer("owner", true, 1, when, false, null)];
        var seasons = new[] { 1, 2, 3, 0 }.Select(number => new MediaServerItem($"season-{number}", "tv", $"Season {number}", 2005, null, null, null, [], Now.AddDays(-100), MediaServerWatchState.Unplayed)
        {
            Level = MediaServerItemLevels.Season,
            ShowServerItemId = "show-1",
            SeasonNumber = number
        });
        return (ServerShow(), [.. seasons, ServerEpisode("e1", 1, 1, Viewed(season1)), ServerEpisode("e2", 2, 1, Viewed(season2)), ServerEpisode("e3", 3, 1, Viewed(season3)), ServerEpisode("e0", 0, 1, [])]);
    }

    private (RetentionUnitReader Reader, Mock<IMovieCatalogRepository> Movies) Reader(string? movieOverride, string? showOverride)
    {
        var movieLibrary = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        movieLibrary = movieLibrary with { Id = "lib-movies", MediaType = "movies", RootPath = "/deluno/movies", QualityProfileName = "Standard" };
        var tvLibrary = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        tvLibrary = tvLibrary with { Id = "lib-tv", MediaType = "tv", RootPath = "/deluno/tv", QualityProfileName = "Standard" };
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([movieLibrary, tvLibrary]);

        var movies = new Mock<IMovieCatalogRepository>();
        var tracked = (MovieTrackedFileItem)RuntimeHelpers.GetUninitializedObject(typeof(MovieTrackedFileItem));
        tracked = tracked with { MovieId = "m1", FilePath = FilePath };
        movies.Setup(repo => repo.StreamTrackedFilesAsync("lib-movies", It.IsAny<CancellationToken>())).Returns(() => new[] { tracked }.ToAsyncEnumerable());
        var movie = (MovieListItem)RuntimeHelpers.GetUninitializedObject(typeof(MovieListItem));
        movie = movie with { Id = "m1", Title = "Nosferatu", ReleaseYear = 1922, Monitored = true, CreatedUtc = Now };
        movies.Setup(repo => repo.ListPageAsync(It.IsAny<CatalogueQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CataloguePage<MovieListItem>([movie], null, false, 1, null));
        movies.Setup(repo => repo.GetQualityProfileIdAsync("m1", It.IsAny<CancellationToken>())).ReturnsAsync(movieOverride);

        var series = new Mock<ISeriesCatalogRepository>();
        var episodeFile = (SeriesTrackedFileItem)RuntimeHelpers.GetUninitializedObject(typeof(SeriesTrackedFileItem));
        episodeFile = episodeFile with { SeriesId = "s1", EpisodeId = "e1", SeasonNumber = 1, EpisodeNumber = 1, FilePath = "/deluno/tv/x.mkv" };
        series.Setup(repo => repo.StreamTrackedFilesAsync("lib-tv", It.IsAny<CancellationToken>())).Returns(() => new[] { episodeFile }.ToAsyncEnumerable());
        var show = (SeriesListItem)RuntimeHelpers.GetUninitializedObject(typeof(SeriesListItem));
        show = show with { Id = "s1", Title = "Show", StartYear = 2020, Monitored = false, SeriesType = "anime", CreatedUtc = Now };
        series.Setup(repo => repo.ListPageAsync(It.IsAny<CatalogueQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CataloguePage<SeriesListItem>([show], null, false, 1, null));
        series.Setup(repo => repo.GetQualityProfileIdAsync("s1", It.IsAny<CancellationToken>())).ReturnsAsync(showOverride);

        var quality = new Mock<IQualityRepository>();
        var remux = (QualityProfileItem)RuntimeHelpers.GetUninitializedObject(typeof(QualityProfileItem));
        remux = remux with { Id = "p-remux", Name = "Remux" };
        quality.Setup(repo => repo.ListQualityProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([remux]);

        return (new RetentionUnitReader(movies.Object, series.Object, libraries.Object, TimeProvider.System, quality.Object), movies);
    }

    private sealed class NullDisk : IRetentionDiskSpace
    {
        public (long FreeBytes, long TotalBytes)? Read(string path) => null;
    }
}
