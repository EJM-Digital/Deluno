using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// The two quality-recalculate handlers (film and TV) when the database fails, when the job is cancelled and when the same
/// profile edit queues the job twice (coverage register, background workers inventory, #1329). A failure fails the job and
/// writes no "refreshed" line; the cancellation token reaches the repository; a repeat does the same work again and writes
/// its own honest count, and queues nothing new (the handlers have no scheduler to stack work with).
/// </summary>
public sealed class TheQualityRecalculateHandlersSurviveFailuresAndRepeatsTests
{
    private const string Payload =
        """
        {"libraryId":"lib-1","libraryName":"Movies","mediaType":"movies","cutoffQuality":"WEB 1080p","upgradeUntilCutoff":true,"upgradeUnknownItems":false}
        """;

    private static Mock<IActivityFeedRepository> Feed(List<string> lines)
    {
        var feed = new Mock<IActivityFeedRepository>();
        feed.Setup(f => f.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((_, message, _, _, _, _, _) => lines.Add(message))
            .ReturnsAsync((ActivityEventItem)null!);
        return feed;
    }

    [Fact]
    public async Task A_database_error_while_a_film_library_is_recalculated_fails_the_job_and_writes_no_refreshed_line()
    {
        var repository = new Mock<IMovieCatalogRepository>();
        repository
            .Setup(r => r.ReevaluateLibraryWantedStateAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        var lines = new List<string>();
        var handler = new MoviesQualityRecalculateJobHandler(repository.Object, Feed(lines).Object);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(TestJobs.Create("movies.quality.recalculate", payloadJson: Payload), CancellationToken.None));

        Assert.Equal("database is locked", error.Message);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task A_database_error_while_a_TV_library_is_recalculated_fails_the_job_and_writes_no_refreshed_line()
    {
        var repository = new Mock<ISeriesCatalogRepository>();
        repository
            .Setup(r => r.ReevaluateLibraryWantedStateAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        var lines = new List<string>();
        var handler = new SeriesQualityRecalculateJobHandler(repository.Object, Feed(lines).Object);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(TestJobs.Create("series.quality.recalculate", payloadJson: Payload), CancellationToken.None));

        Assert.Equal("database is locked", error.Message);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task A_cancelled_film_recalculation_hands_the_token_to_the_repository_and_records_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var repository = new Mock<IMovieCatalogRepository>();
        repository
            .Setup(r => r.ReevaluateLibraryWantedStateAsync("lib-1", "WEB 1080p", true, false, It.Is<CancellationToken>(t => t.IsCancellationRequested)))
            .ThrowsAsync(new OperationCanceledException());
        var lines = new List<string>();
        var handler = new MoviesQualityRecalculateJobHandler(repository.Object, Feed(lines).Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(TestJobs.Create("movies.quality.recalculate", payloadJson: Payload), cancelled.Token));

        Assert.Empty(lines);
    }

    [Fact]
    public async Task A_cancelled_TV_recalculation_hands_the_token_to_the_repository_and_records_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var repository = new Mock<ISeriesCatalogRepository>();
        repository
            .Setup(r => r.ReevaluateLibraryWantedStateAsync("lib-1", "WEB 1080p", true, false, It.Is<CancellationToken>(t => t.IsCancellationRequested)))
            .ThrowsAsync(new OperationCanceledException());
        var lines = new List<string>();
        var handler = new SeriesQualityRecalculateJobHandler(repository.Object, Feed(lines).Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(TestJobs.Create("series.quality.recalculate", payloadJson: Payload), cancelled.Token));

        Assert.Empty(lines);
    }

    [Fact]
    public async Task The_same_film_profile_edit_run_twice_recalculates_twice_with_the_same_arguments_and_each_run_says_its_own_count()
    {
        var repository = new Mock<IMovieCatalogRepository>();
        repository
            .SetupSequence(r => r.ReevaluateLibraryWantedStateAsync("lib-1", "WEB 1080p", true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3)
            .ReturnsAsync(0);
        var lines = new List<string>();
        var handler = new MoviesQualityRecalculateJobHandler(repository.Object, Feed(lines).Object);

        var first = await handler.HandleAsync(TestJobs.Create("movies.quality.recalculate", payloadJson: Payload), CancellationToken.None);
        var second = await handler.HandleAsync(TestJobs.Create("movies.quality.recalculate", payloadJson: Payload), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(2, lines.Count);
        Assert.Contains("across 3 movie records", lines[0]);
        Assert.Contains("across 0 movie records", lines[1]);
    }

    [Fact]
    public async Task The_same_TV_profile_edit_run_twice_recalculates_twice_with_the_same_arguments_and_each_run_says_its_own_count()
    {
        var repository = new Mock<ISeriesCatalogRepository>();
        repository
            .SetupSequence(r => r.ReevaluateLibraryWantedStateAsync("lib-1", "WEB 1080p", true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2)
            .ReturnsAsync(0);
        var lines = new List<string>();
        var handler = new SeriesQualityRecalculateJobHandler(repository.Object, Feed(lines).Object);

        var first = await handler.HandleAsync(TestJobs.Create("series.quality.recalculate", payloadJson: Payload), CancellationToken.None);
        var second = await handler.HandleAsync(TestJobs.Create("series.quality.recalculate", payloadJson: Payload), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(2, lines.Count);
        Assert.Contains("across 2 TV show records", lines[0]);
        Assert.Contains("across 0 TV show records", lines[1]);
    }
}
