using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Retention;

namespace Deluno.Worker.Tests.Retention;

/// <summary>
/// #834: sinceAdded variants of the watched-by fields, the lastViewed/lastPlayed
/// split, and part.qualityRank, exercised directly against
/// <see cref="RetentionFieldAnswers"/> so a field's answer can be checked
/// without standing up a whole rule and catalogue.
/// </summary>
public sealed class RetentionAnswersTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T12:00:00Z");
    private static readonly MediaServerConnection Server = new("server-1", "jellyfin", "Rig lab Jellyfin", "http://127.0.0.1:8096", "key", true, "healthy", null, null, null, "WIN-RIG", "id", Now, Now);

    [Fact]
    public void Watched_by_since_added_only_counts_plays_after_the_title_joined_deluno()
    {
        var unit = Movie(addedUtc: Now.AddDays(-10));
        var item = new MediaServerItem("j1", "movies", "Nosferatu", 1922, "653", "tt0013442", null, [Path], Now.AddDays(-100), MediaServerWatchState.Unplayed)
        {
            Viewers =
            [
                new MediaServerViewer("owner", true, 1, Now.AddDays(-20), false, null),
                new MediaServerViewer("ben", true, 1, Now.AddDays(-5), false, null)
            ]
        };
        var answers = Answers(item);
        var field = RetentionFieldCatalog.Find("server.watchedBySinceAdded")!;

        var answer = answers.Answer(unit, field, RetentionOperators.Anyone, null);

        Assert.True(answer.Holds);
        Assert.Contains("ben", answer.Sentence);
        Assert.DoesNotContain("owner", answer.Sentence);
    }

    [Fact]
    public void Watched_by_since_added_cannot_say_when_deluno_does_not_know_when_it_was_added()
    {
        var unit = Movie(addedUtc: null);
        var item = new MediaServerItem("j1", "movies", "Nosferatu", 1922, "653", "tt0013442", null, [Path], Now.AddDays(-100), MediaServerWatchState.Unplayed)
        {
            Viewers = [new MediaServerViewer("owner", true, 1, Now.AddDays(-5), false, null)]
        };
        var answers = Answers(item);
        var field = RetentionFieldCatalog.Find("server.watchedBySinceAdded")!;

        var answer = answers.Answer(unit, field, RetentionOperators.Anyone, null);

        Assert.Null(answer.Holds);
        Assert.Contains("does not know when it was added", answer.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Last_viewed_is_finished_only_and_last_played_includes_unfinished()
    {
        var unit = Movie(addedUtc: Now.AddDays(-100));
        var item = new MediaServerItem("j1", "movies", "Nosferatu", 1922, "653", "tt0013442", null, [Path], Now.AddDays(-100), MediaServerWatchState.Unplayed)
        {
            Viewers =
            [
                new MediaServerViewer("owner", true, 1, Now.AddDays(-40), false, null),
                // Started, never finished, but played more recently.
                new MediaServerViewer("ben", false, 0, Now.AddDays(-2), true, null)
            ]
        };
        var answers = Answers(item);
        var lastViewed = RetentionFieldCatalog.Find("server.lastViewed")!;
        var lastPlayed = RetentionFieldCatalog.Find("server.lastPlayed")!;

        var viewedAnswer = answers.Answer(unit, lastViewed, "within", "10");
        var playedAnswer = answers.Answer(unit, lastPlayed, "within", "10");

        // Only the owner's finished play counts for lastViewed, and it is 40 days ago.
        Assert.False(viewedAnswer.Holds);
        // Ben's unfinished play, 2 days ago, counts for lastPlayed.
        Assert.True(playedAnswer.Holds);
    }

    [Fact]
    public void Quality_rank_reads_the_quality_models_numeric_tier()
    {
        var unit = Episode(quality: "HD-1080p");
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["SD"] = 1, ["HD-1080p"] = 5 };
        var answers = new RetentionFieldAnswers([], new NullDisk(), Now, null, ranks);
        var field = RetentionFieldCatalog.Find("part.qualityRank")!;

        var answer = answers.Answer(unit, field, "min", "3");

        Assert.True(answer.Holds);
        Assert.Contains(": 5", answer.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Quality_rank_cannot_say_when_the_tier_is_not_in_the_model()
    {
        var unit = Episode(quality: "Some-unknown-tier");
        var answers = new RetentionFieldAnswers([], new NullDisk(), Now, null, new Dictionary<string, int>());
        var field = RetentionFieldCatalog.Find("part.qualityRank")!;

        var answer = answers.Answer(unit, field, "min", "3");

        Assert.Null(answer.Holds);
    }

    // #834: server.watchersSinceAdded and server.allEpisodesSeenBySinceAdded --
    // the same since-added filter server.watchedBySinceAdded already gets
    // (above), but computed across a show's episodes rather than one item.

    [Fact]
    public void Watchers_since_added_counts_anyone_who_finished_an_episode_after_the_show_joined_deluno()
    {
        var unit = Show(addedUtc: Now.AddDays(-10));
        var (show, episodes) = ShowWithEpisodes(
            // the owner finished episode 1 before the show joined Deluno -- does not count.
            episode1Viewers: [new MediaServerViewer("owner", true, 1, Now.AddDays(-20), false, null)],
            // Ben finished episode 2 after -- counts.
            episode2Viewers: [new MediaServerViewer("ben", true, 1, Now.AddDays(-5), false, null)]);

        var answers = Answers([show, .. episodes]);
        var field = RetentionFieldCatalog.Find("server.watchersSinceAdded")!;

        var answer = answers.Answer(unit, field, RetentionOperators.Anyone, null);

        Assert.True(answer.Holds);
        Assert.Contains("ben", answer.Sentence);
        Assert.DoesNotContain("owner", answer.Sentence);
    }

    [Fact]
    public void Watchers_since_added_cannot_say_when_deluno_does_not_know_when_the_show_was_added()
    {
        var unit = Show(addedUtc: null);
        var (show, episodes) = ShowWithEpisodes(
            episode1Viewers: [new MediaServerViewer("owner", true, 1, Now.AddDays(-5), false, null)],
            episode2Viewers: []);

        var answers = Answers([show, .. episodes]);
        var field = RetentionFieldCatalog.Find("server.watchersSinceAdded")!;

        var answer = answers.Answer(unit, field, RetentionOperators.Anyone, null);

        Assert.Null(answer.Holds);
        Assert.Contains("does not know when it was added", answer.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void All_episodes_seen_by_since_added_requires_every_episode_finished_after_the_show_joined_deluno()
    {
        var unit = Show(addedUtc: Now.AddDays(-10));
        var (show, episodes) = ShowWithEpisodes(
            // the owner finished both episodes, but episode 1 was before the show joined Deluno.
            episode1Viewers: [new MediaServerViewer("owner", true, 1, Now.AddDays(-20), false, null)],
            episode2Viewers: [new MediaServerViewer("owner", true, 1, Now.AddDays(-5), false, null)]);

        var answers = Answers([show, .. episodes]);
        var field = RetentionFieldCatalog.Find("server.allEpisodesSeenBySinceAdded")!;

        var answer = answers.Answer(unit, field, RetentionOperators.Anyone, null);

        // Nobody has finished *every* episode since the show was added --
        // episode 1's play predates it -- so the all-time field would say
        // the owner finished it all, but the since-added one must not.
        Assert.False(answer.Holds);
        Assert.Contains("nobody", answer.Sentence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void All_episodes_seen_by_since_added_holds_once_every_episode_was_finished_after_added()
    {
        var unit = Show(addedUtc: Now.AddDays(-10));
        var (show, episodes) = ShowWithEpisodes(
            episode1Viewers: [new MediaServerViewer("owner", true, 1, Now.AddDays(-7), false, null)],
            episode2Viewers: [new MediaServerViewer("owner", true, 1, Now.AddDays(-5), false, null)]);

        var answers = Answers([show, .. episodes]);
        var field = RetentionFieldCatalog.Find("server.allEpisodesSeenBySinceAdded")!;

        var answer = answers.Answer(unit, field, RetentionOperators.Anyone, null);

        Assert.True(answer.Holds);
        Assert.Contains("owner", answer.Sentence);
    }

    private const string Path = @"C:\Deluno\RetentionLab\Movies\Nosferatu (1922)\Nosferatu (1922).mkv";
    private const string ShowTmdbId = "12345";

    private static RetentionFieldAnswers Answers(params MediaServerItem[] items)
        => new([new RetentionServerIndex(Server, new MediaServerLibrary(items, ["owner", "ben"]))], new NullDisk(), Now);

    private static RetentionUnit Movie(DateTimeOffset? addedUtc)
        => new(RetentionLevels.Movie, "m1", null, null, null, "Nosferatu", "Nosferatu", 1922, "tt0013442", "653", null, 1024, [Path], [], AddedUtc: addedUtc);

    private static RetentionUnit Show(DateTimeOffset? addedUtc)
        => new(RetentionLevels.Show, "s1", null, null, null, "Land of the Dead", "Land of the Dead", 2005, null, ShowTmdbId, null, 1024, ["/deluno/tv/Land of the Dead"], [], AddedUtc: addedUtc);

    private static (MediaServerItem Show, MediaServerItem[] Episodes) ShowWithEpisodes(
        IReadOnlyList<MediaServerViewer> episode1Viewers,
        IReadOnlyList<MediaServerViewer> episode2Viewers)
    {
        var show = new MediaServerItem("show-1", "tv", "Land of the Dead", 2005, ShowTmdbId, null, null, ["/deluno/tv/Land of the Dead"], Now.AddDays(-100), MediaServerWatchState.Unplayed)
        {
            Level = MediaServerItemLevels.Show
        };
        var episode1 = new MediaServerItem("ep-1", "tv", "Land of the Dead — S01E01", 2005, null, null, null, [], Now.AddDays(-100), MediaServerWatchState.Unplayed)
        {
            Level = MediaServerItemLevels.Episode,
            ShowServerItemId = "show-1",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Viewers = episode1Viewers
        };
        var episode2 = new MediaServerItem("ep-2", "tv", "Land of the Dead — S01E02", 2005, null, null, null, [], Now.AddDays(-100), MediaServerWatchState.Unplayed)
        {
            Level = MediaServerItemLevels.Episode,
            ShowServerItemId = "show-1",
            SeasonNumber = 1,
            EpisodeNumber = 2,
            Viewers = episode2Viewers
        };
        return (show, [episode1, episode2]);
    }

    private static RetentionUnit Episode(string quality)
        => new(RetentionLevels.Episode, "e1", "s1", 1, 1, "Show — S01E01", "Show", 2020, null, null, null, 1024, [Path], [],
            new RetentionPartFacts(true, false, 1, null, Now.AddDays(-5), quality, true, true, Path, ["e1"]));

    private sealed class NullDisk : IRetentionDiskSpace
    {
        public (long FreeBytes, long TotalBytes)? Read(string path) => null;
    }
}
