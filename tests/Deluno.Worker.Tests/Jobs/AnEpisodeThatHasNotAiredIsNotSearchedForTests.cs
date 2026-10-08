using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #1173: an episode that has not aired is not searched for, with a day's margin for the usual early release. The
/// skip is written on the episode in words ("airs on ..."), and an episode inside the margin is searched as normal.
/// </summary>
public sealed class AnEpisodeThatHasNotAiredIsNotSearchedForTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");

    private sealed class FixedClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    [Fact]
    public async Task An_episode_airing_more_than_a_day_ahead_is_skipped_with_an_airs_on_message()
    {
        var run = await RunAsync(Now.AddDays(5));

        Assert.Empty(run.Requests);
        Assert.Equal("Skipped episode S02E03: it has not aired yet.", run.Result);
        var note = Assert.Single(run.Notes);
        Assert.Contains("S02E03 airs on 13 October", note, StringComparison.Ordinal);
        Assert.Contains("will not look for it before then", note, StringComparison.Ordinal);
        // Looked at again the day before it airs, not a retry window away.
        Assert.Equal(Now.AddDays(4), run.NextLook);
    }

    [Fact]
    public async Task An_episode_airing_within_the_margin_is_searched()
    {
        var run = await RunAsync(Now.AddHours(20));

        Assert.Single(run.Requests);
        Assert.DoesNotContain(run.Notes, note => note!.Contains("airs on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_episode_that_has_already_aired_is_searched()
    {
        var run = await RunAsync(Now.AddDays(-3));

        Assert.Single(run.Requests);
        Assert.DoesNotContain(run.Notes, note => note!.Contains("airs on", StringComparison.Ordinal));
    }

    private sealed record Run(string Result, List<AcquisitionDecisionRequest> Requests, List<string?> Notes, DateTimeOffset? NextLook);

    private static async Task<Run> RunAsync(DateTimeOffset airs)
    {
        var requests = new List<AcquisitionDecisionRequest>();
        var pipeline = new Mock<IAcquisitionDecisionPipeline>();
        pipeline
            .Setup(item => item.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AcquisitionDecisionRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync(new AcquisitionDecisionPlan(
                new MediaSearchPlan(null, [], "Nothing found."),
                Deluno.Quality.MediaPolicyCatalog.CurrentVersion,
                "checked", "Nothing found.", 1, 1, null, false, null, []));

        var notes = new List<string?>();
        DateTimeOffset? nextLook = null;
        var episodeItem = new SeriesEpisodeInventoryItem(
            "e-203", 2, 3, "Third", null, airs, true, false, "missing", "not found", false, null, "WEB 1080p",
            false, null, null, null, Now);
        var series = new Mock<ISeriesCatalogRepository>();
        series
            .Setup(item => item.GetInventoryDetailAsync("series-x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesInventoryDetail("series-x", "Example Show", 2020, 2, 1, 0, [episodeItem]));
        series
            .Setup(item => item.GetByIdAsync("series-x", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesListItem)RuntimeHelpers.GetUninitializedObject(typeof(SeriesListItem)) with
            {
                Id = "series-x",
                Title = "Example Show"
            });
        series
            .Setup(item => item.RecordSearchAttemptAsync(
                "series-x", "e-203", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string?, string, string, string, DateTimeOffset, DateTimeOffset?, string?, string?, string?, string?, CancellationToken>(
                (_, _, _, _, _, _, next, note, _, _, _, _) =>
                {
                    nextLook = next;
                    notes.Add(note);
                })
            .Returns(Task.CompletedTask);

        var library = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with
        {
            Id = "library-tv",
            Name = "TV",
            MediaType = "tv",
            QualityProfileId = "profile-standard",
            RetryDelayHours = 1
        };
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(item => item.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        var quality = new Mock<IQualityRepository>();
        quality.Setup(item => item.ListCustomFormatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        quality
            .Setup(item => item.ListQualityProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new QualityProfileItem(
                "profile-standard", "Standard", "tv", "WEB 1080p", "WEB 1080p,Bluray 1080p", string.Empty,
                true, false, false, null, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]);

        var handler = new EpisodeSearchJobHandler(
            libraries.Object,
            quality.Object,
            series.Object,
            Mock.Of<IJobQueueRepository>(),
            pipeline.Object,
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            new FixedClock(Now));
        var payload = JsonSerializer.Serialize(new
        {
            episodeId = "e-203",
            seriesId = "series-x",
            libraryId = "library-tv",
            seasonNumber = 2,
            episodeNumber = 3,
            title = "Third"
        });

        var result = await handler.HandleAsync(TestJobs.Create("episode.search", payloadJson: payload), CancellationToken.None);
        return new Run(result, requests, notes, nextLook);
    }
}
