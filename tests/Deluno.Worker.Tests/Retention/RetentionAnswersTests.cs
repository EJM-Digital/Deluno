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
                new MediaServerViewer("james", true, 1, Now.AddDays(-20), false, null),
                new MediaServerViewer("ben", true, 1, Now.AddDays(-5), false, null)
            ]
        };
        var answers = Answers(item);
        var field = RetentionFieldCatalog.Find("server.watchedBySinceAdded")!;

        var answer = answers.Answer(unit, field, RetentionOperators.Anyone, null);

        Assert.True(answer.Holds);
        Assert.Contains("ben", answer.Sentence);
        Assert.DoesNotContain("james", answer.Sentence);
    }

    [Fact]
    public void Watched_by_since_added_cannot_say_when_deluno_does_not_know_when_it_was_added()
    {
        var unit = Movie(addedUtc: null);
        var item = new MediaServerItem("j1", "movies", "Nosferatu", 1922, "653", "tt0013442", null, [Path], Now.AddDays(-100), MediaServerWatchState.Unplayed)
        {
            Viewers = [new MediaServerViewer("james", true, 1, Now.AddDays(-5), false, null)]
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
                new MediaServerViewer("james", true, 1, Now.AddDays(-40), false, null),
                // Started, never finished, but played more recently.
                new MediaServerViewer("ben", false, 0, Now.AddDays(-2), true, null)
            ]
        };
        var answers = Answers(item);
        var lastViewed = RetentionFieldCatalog.Find("server.lastViewed")!;
        var lastPlayed = RetentionFieldCatalog.Find("server.lastPlayed")!;

        var viewedAnswer = answers.Answer(unit, lastViewed, "within", "10");
        var playedAnswer = answers.Answer(unit, lastPlayed, "within", "10");

        // Only James's finished play counts for lastViewed, and it is 40 days ago.
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

    private const string Path = @"C:\Deluno\RetentionLab\Movies\Nosferatu (1922)\Nosferatu (1922).mkv";

    private static RetentionFieldAnswers Answers(params MediaServerItem[] items)
        => new([new RetentionServerIndex(Server, new MediaServerLibrary(items, ["james", "ben"]))], new NullDisk(), Now);

    private static RetentionUnit Movie(DateTimeOffset? addedUtc)
        => new(RetentionLevels.Movie, "m1", null, null, null, "Nosferatu", "Nosferatu", 1922, "tt0013442", "653", null, 1024, [Path], [], AddedUtc: addedUtc);

    private static RetentionUnit Episode(string quality)
        => new(RetentionLevels.Episode, "e1", "s1", 1, 1, "Show — S01E01", "Show", 2020, null, null, null, 1024, [Path], [],
            new RetentionPartFacts(true, false, 1, null, Now.AddDays(-5), quality, true, true, Path, ["e1"]));

    private sealed class NullDisk : IRetentionDiskSpace
    {
        public (long FreeBytes, long TotalBytes)? Read(string path) => null;
    }
}
