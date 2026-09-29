using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Retention;

namespace Deluno.Worker.Tests.Retention;

/// <summary>
/// #1068: a value nobody reported is undecided for every test on it, never a match.
/// A rule that says "codec is not HEVC" must not select a film whose codec the
/// server never said. The explicit "is set" / "is not set" tests are the only
/// ones that may hold for a missing value.
/// </summary>
public sealed class RetentionUnknownAnswerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
    private static readonly MediaServerConnection Server = new("server-1", "jellyfin", "Rig lab Jellyfin", "http://127.0.0.1:8096", "key", true, "healthy", null, null, null, "WIN-RIG", "id", Now, Now);
    private const string FilmPath = @"C:\Deluno\RetentionLab\Movies\Nosferatu (1922)\Nosferatu (1922).mkv";

    // ---------------------------------------------- the server's text fields

    [Theory]
    [InlineData("server.resolution", "has", "2160")]
    [InlineData("server.resolution", "nothas", "2160")]
    [InlineData("server.resolution", "is", "2160p")]
    [InlineData("server.resolution", "isnot", "2160p")]
    [InlineData("server.codec", "has", "hevc")]
    [InlineData("server.codec", "nothas", "hevc")]
    [InlineData("server.codec", "is", "hevc")]
    [InlineData("server.codec", "isnot", "hevc")]
    public void A_text_the_server_never_reported_is_undecided_for_every_test(string field, string op, string value)
    {
        var answer = Ask(FilmUnit(), new MediaServerItem("j1", "movies", "Nosferatu", 1922, "653", null, null, [FilmPath], null, MediaServerWatchState.Unplayed), field, op, value);

        Assert.Null(answer.Holds);
        Assert.Contains("not known", answer.Sentence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("server.resolution", "set", false)]
    [InlineData("server.resolution", "unset", true)]
    [InlineData("server.codec", "set", false)]
    [InlineData("server.codec", "unset", true)]
    public void Only_the_explicit_is_set_and_is_not_set_tests_decide_a_missing_text(string field, string op, bool expected)
    {
        var answer = Ask(FilmUnit(), new MediaServerItem("j1", "movies", "Nosferatu", 1922, "653", null, null, [FilmPath], null, MediaServerWatchState.Unplayed), field, op, null);

        Assert.Equal(expected, answer.Holds);
    }

    [Theory]
    [InlineData("has", "hevc", true)]
    [InlineData("has", "av1", false)]
    [InlineData("nothas", "hevc", false)]
    [InlineData("nothas", "av1", true)]
    [InlineData("is", "hevc|av1", true)]
    [InlineData("is", "av1", false)]
    [InlineData("isnot", "hevc", false)]
    [InlineData("isnot", "av1|h264", true)]
    [InlineData("set", "", true)]
    [InlineData("unset", "", false)]
    public void A_text_the_server_did_report_is_still_decided(string op, string value, bool expected)
    {
        var item = WithVideo(new MediaServerVideo("1080p", 8000, "HEVC"));

        var answer = Ask(FilmUnit(), item, "server.codec", op, value);

        Assert.Equal(expected, answer.Holds);
    }

    // ---------------------------------------------- the server's number fields

    [Theory]
    [InlineData("server.bitrate", "min", "5000")]
    [InlineData("server.bitrate", "max", "5000")]
    [InlineData("server.bitrate", "is", "5000")]
    [InlineData("server.bitrate", "isnot", "5000")]
    [InlineData("server.ratingTmdb", "min", "7")]
    [InlineData("server.ratingTmdb", "max", "7")]
    [InlineData("server.ratingTmdb", "is", "7")]
    [InlineData("server.ratingTmdb", "isnot", "7")]
    [InlineData("server.userRating", "isnot", "10")]
    public void A_number_the_server_never_reported_is_undecided_for_every_test(string field, string op, string value)
    {
        var answer = Ask(FilmUnit(), WithVideo(MediaServerVideo.None), field, op, value);

        Assert.Null(answer.Holds);
    }

    [Theory]
    [InlineData("set", false)]
    [InlineData("unset", true)]
    public void Only_the_explicit_is_set_and_is_not_set_tests_decide_a_missing_number(string op, bool expected)
    {
        var answer = Ask(FilmUnit(), WithVideo(MediaServerVideo.None), "server.bitrate", op, null);

        Assert.Equal(expected, answer.Holds);
    }

    [Theory]
    [InlineData("min", "5000", true)]
    [InlineData("min", "9000", false)]
    [InlineData("max", "9000", true)]
    [InlineData("max", "5000", false)]
    [InlineData("is", "8000", true)]
    [InlineData("isnot", "8000", false)]
    [InlineData("isnot", "5000", true)]
    public void A_number_the_server_did_report_is_still_decided(string op, string value, bool expected)
    {
        var answer = Ask(FilmUnit(), WithVideo(new MediaServerVideo("1080p", 8000, "hevc")), "server.bitrate", op, value);

        Assert.Equal(expected, answer.Holds);
    }

    // ---------------------------------------------- lists

    [Theory]
    [InlineData("server.genres", "in", "Horror")]
    [InlineData("server.genres", "all", "Horror")]
    [InlineData("server.genres", "notin", "Horror")]
    [InlineData("server.people", "in", "Max Schreck")]
    [InlineData("server.people", "all", "Max Schreck")]
    [InlineData("server.people", "notin", "Max Schreck")]
    public void A_genre_or_cast_list_the_server_left_empty_is_undecided(string field, string op, string value)
    {
        var answer = Ask(FilmUnit(), WithVideo(MediaServerVideo.None), field, op, value);

        Assert.Null(answer.Holds);
    }

    [Theory]
    [InlineData("set", false)]
    [InlineData("unset", true)]
    public void An_empty_genre_list_is_still_answered_by_is_empty_and_is_not_empty(string op, bool expected)
    {
        Assert.Equal(expected, Ask(FilmUnit(), WithVideo(MediaServerVideo.None), "server.genres", op, null).Holds);
    }

    [Theory]
    [InlineData("server.collections", "in", false)]
    [InlineData("server.collections", "notin", true)]
    [InlineData("server.labels", "notin", true)]
    [InlineData("server.playlists", "notin", true)]
    public void A_collection_label_or_playlist_list_is_complete_so_empty_means_none(string field, string op, bool expected)
    {
        Assert.Equal(expected, Ask(FilmUnit(), WithVideo(MediaServerVideo.None), field, op, "Anything").Holds);
    }

    [Fact]
    public void A_genre_list_the_server_did_fill_in_is_still_decided()
    {
        var item = WithVideo(MediaServerVideo.None) with { Genres = ["Horror", "Silent"] };

        Assert.True(Ask(FilmUnit(), item, "server.genres", "in", "Horror").Holds);
        Assert.False(Ask(FilmUnit(), item, "server.genres", "notin", "Horror").Holds);
        Assert.True(Ask(FilmUnit(), item, "server.genres", "notin", "Comedy").Holds);
    }

    // ---------------------------------------------- dates: never a match

    [Theory]
    [InlineData("before", "2000-01-01")]
    [InlineData("after", "2000-01-01")]
    [InlineData("beyond", "30")]
    [InlineData("within", "30")]
    [InlineData("next", "30")]
    public void A_date_the_server_never_reported_is_never_a_match(string op, string value)
    {
        var answer = Ask(FilmUnit(), WithVideo(MediaServerVideo.None), "server.releaseDate", op, value);

        Assert.NotEqual(true, answer.Holds);
    }

    [Theory]
    [InlineData("set", false)]
    [InlineData("unset", true)]
    public void Only_the_explicit_is_set_and_is_not_set_tests_decide_a_missing_date(string op, bool expected)
    {
        Assert.Equal(expected, Ask(FilmUnit(), WithVideo(MediaServerVideo.None), "server.releaseDate", op, null).Holds);
    }

    // ---------------------------------------------- Deluno's own season and episode facts

    [Theory]
    [InlineData("part.quality", "has", "1080")]
    [InlineData("part.quality", "nothas", "1080")]
    [InlineData("part.quality", "is", "HD-1080p")]
    [InlineData("part.quality", "isnot", "HD-1080p")]
    [InlineData("part.path", "has", "Season 1")]
    [InlineData("part.path", "nothas", "Season 1")]
    [InlineData("part.path", "is", "/tv/a.mkv")]
    [InlineData("part.path", "isnot", "/tv/a.mkv")]
    [InlineData("part.seasonNumber", "isnot", "2")]
    [InlineData("part.seasonNumber", "min", "2")]
    [InlineData("part.episodeNumber", "isnot", "2")]
    [InlineData("part.episodeNumber", "max", "2")]
    [InlineData("part.size", "isnot", "5")]
    [InlineData("part.size", "max", "5")]
    [InlineData("part.cutoffMet", "is", "true")]
    [InlineData("part.monitored", "is", "false")]
    public void An_episode_fact_deluno_does_not_hold_is_undecided_for_every_test(string field, string op, string value)
    {
        var unit = new RetentionUnit(RetentionLevels.Episode, "e1", "s1", null, null, "Show", "Show", 2020, null, null, null, null, [], [],
            new RetentionPartFacts(true, false, 1, null, null, null, null, null, null, ["e1"]));

        var answer = new RetentionFieldAnswers([], new NullDisk(), Now).Answer(unit, RetentionFieldCatalog.Find(field)!, op, value);

        Assert.Null(answer.Holds);
    }

    [Theory]
    [InlineData("part.quality", "set", false)]
    [InlineData("part.quality", "unset", true)]
    [InlineData("part.path", "set", false)]
    [InlineData("part.path", "unset", true)]
    [InlineData("part.size", "set", false)]
    [InlineData("part.size", "unset", true)]
    [InlineData("part.airDate", "set", false)]
    [InlineData("part.airDate", "unset", true)]
    public void Only_the_explicit_is_set_and_is_not_set_tests_decide_a_missing_episode_fact(string field, string op, bool expected)
    {
        var unit = new RetentionUnit(RetentionLevels.Episode, "e1", "s1", 1, 1, "Show", "Show", 2020, null, null, null, null, [], [],
            new RetentionPartFacts(true, false, 1, null, null, null, null, null, null, ["e1"]));

        var answer = new RetentionFieldAnswers([], new NullDisk(), Now).Answer(unit, RetentionFieldCatalog.Find(field)!, op, null);

        Assert.Equal(expected, answer.Holds);
    }

    [Theory]
    [InlineData("has", "1080", true)]
    [InlineData("nothas", "1080", false)]
    [InlineData("isnot", "HD-720p", true)]
    [InlineData("isnot", "HD-1080p", false)]
    public void An_episode_quality_deluno_does_hold_is_still_decided(string op, string value, bool expected)
    {
        var unit = new RetentionUnit(RetentionLevels.Episode, "e1", "s1", 1, 1, "Show", "Show", 2020, null, null, null, 1024, [], [],
            new RetentionPartFacts(true, false, 1, null, null, "HD-1080p", null, null, null, ["e1"]));

        var answer = new RetentionFieldAnswers([], new NullDisk(), Now).Answer(unit, RetentionFieldCatalog.Find("part.quality")!, op, value);

        Assert.Equal(expected, answer.Holds);
    }

    // ---------------------------------------------- Deluno's own title facts (guarded before, kept guarded)

    [Theory]
    [InlineData("title.qualityProfile", "isnot", "Any")]
    [InlineData("title.qualityProfile", "nothas", "Any")]
    [InlineData("title.seriesType", "isnot", "anime")]
    [InlineData("title.monitored", "is", "false")]
    [InlineData("title.missingEpisodes", "isnot", "0")]
    [InlineData("title.episodeRank", "min", "6")]
    public void A_title_fact_deluno_does_not_hold_is_undecided(string field, string op, string value)
    {
        var unit = new RetentionUnit(RetentionLevels.Show, "s1", null, null, null, "Show", "Show", 2020, null, null, null, null, [], [],
            Facts: new RetentionTitleFacts(null, null, null, null, null, null, null, null));

        var answer = new RetentionFieldAnswers([], new NullDisk(), Now).Answer(unit, RetentionFieldCatalog.Find(field)!, op, value);

        Assert.Null(answer.Holds);
    }

    private static RetentionAnswer Ask(RetentionUnit unit, MediaServerItem item, string field, string op, string? value)
        => new RetentionFieldAnswers([new RetentionServerIndex(Server, new MediaServerLibrary([item], ["owner"]))], new NullDisk(), Now)
            .Answer(unit, RetentionFieldCatalog.Find(field)!, op, value);

    private static MediaServerItem WithVideo(MediaServerVideo video)
        => new MediaServerItem("j1", "movies", "Nosferatu", 1922, "653", null, null, [FilmPath], null, MediaServerWatchState.Unplayed) { Video = video };

    private static RetentionUnit FilmUnit()
        => new(RetentionLevels.Movie, "m1", null, null, null, "Nosferatu", "Nosferatu", 1922, null, "653", null, 1024, [FilmPath], []);

    private sealed class NullDisk : IRetentionDiskSpace
    {
        public (long FreeBytes, long TotalBytes)? Read(string path) => null;
    }
}
