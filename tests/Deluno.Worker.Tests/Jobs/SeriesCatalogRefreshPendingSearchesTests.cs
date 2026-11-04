using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>#645: a show's own search is queued by the catalogue refresh, once per search it carries, and never when it carries none.</summary>
public sealed class SeriesCatalogRefreshPendingSearchesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");

    private static (SeriesCatalogRefreshJobHandler Handler, List<EnqueueJobRequest> Enqueued) Build()
    {
        var series = new SeriesListItem(
            "series-1", "Severance", 2022, "tt11280740", true, true,
            null, null, null, null, null, null, null, [], null, null, null, null, Now, Now);
        var metadataProvider = new Mock<IMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var repository = new Mock<ISeriesCatalogRepository>();
        repository.Setup(r => r.GetByIdAsync("series-1", It.IsAny<CancellationToken>())).ReturnsAsync(series);
        var enqueued = new List<EnqueueJobRequest>();
        var scheduler = new Mock<IJobScheduler>();
        scheduler
            .Setup(s => s.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => enqueued.Add(request))
            .ReturnsAsync(TestJobs.Create("library.search.tv"));
        var handler = new SeriesCatalogRefreshJobHandler(
            metadataProvider.Object, repository.Object, new Mock<IActivityFeedRepository>(MockBehavior.Strict).Object, scheduler.Object);
        return (handler, enqueued);
    }

    [Theory]
    [InlineData("""{"id":"series-1","pendingSearches":[]}""")]
    [InlineData("""{"id":"series-1"}""")]
    [InlineData("not json")]
    public async Task A_refresh_carrying_no_search_queues_nothing(string payloadJson)
    {
        var (handler, enqueued) = Build();

        await handler.HandleAsync(TestJobs.Create("series.catalog.refresh", payloadJson: payloadJson, relatedEntityId: "series-1"), CancellationToken.None);

        Assert.Empty(enqueued);
    }

    [Fact]
    public async Task Every_search_it_carries_is_queued_in_order_with_its_own_numbers()
    {
        var (handler, enqueued) = Build();
        var payload = JsonSerializer.Serialize(new
        {
            id = "series-1",
            pendingSearches = new[]
            {
                new { libraryId = "lib-a", libraryName = "TV A", mediaType = "tv", retryDelayHours = 6, triggeredBy = "added", targetEntityId = "series-1" },
                new { libraryId = "lib-b", libraryName = "TV B", mediaType = "tv", retryDelayHours = 12, triggeredBy = "added", targetEntityId = "series-1" }
            }
        });

        await handler.HandleAsync(TestJobs.Create("series.catalog.refresh", payloadJson: payload, relatedEntityId: "series-1"), CancellationToken.None);

        Assert.Equal(2, enqueued.Count);
        Assert.All(enqueued, request => Assert.Equal("series", request.Source));
        Assert.Contains("\"libraryId\":\"lib-a\"", enqueued[0].PayloadJson);
        Assert.Contains("\"retryDelayHours\":6", enqueued[0].PayloadJson);
        Assert.Contains("\"libraryName\":\"TV A\"", enqueued[0].PayloadJson);
        Assert.Contains("\"mediaType\":\"tv\"", enqueued[0].PayloadJson);
        Assert.Contains("\"checkMissing\":true", enqueued[0].PayloadJson);
        Assert.Contains("\"checkUpgrades\":true", enqueued[0].PayloadJson);
        Assert.Contains("\"maxItems\":1", enqueued[0].PayloadJson);
        Assert.Contains("\"libraryId\":\"lib-b\"", enqueued[1].PayloadJson);
        Assert.Contains("\"retryDelayHours\":12", enqueued[1].PayloadJson);
    }

    [Fact]
    public void It_handles_the_series_catalogue_refresh_job_type()
    {
        Assert.Equal("series.catalog.refresh", Build().Handler.JobType);
    }
}
