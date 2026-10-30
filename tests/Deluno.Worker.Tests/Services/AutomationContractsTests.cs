using Deluno.Worker.Automation;

namespace Deluno.Worker.Tests.Services;

/// <summary>The automation write shape (bulk add of titles and episodes) and its summary keep exactly what they are given, with the documented defaults.</summary>
public sealed class AutomationContractsTests
{
    [Fact]
    public void The_batch_limits_are_the_published_ones()
    {
        Assert.Equal(250, AutomationBatchLimits.MaxCatalogueItems);
        Assert.Equal(1_000, AutomationBatchLimits.MaxEpisodeItems);
        Assert.Equal(200, AutomationBatchLimits.MaxIdempotencyKeyLength);
    }

    [Fact]
    public void A_bulk_add_request_defaults_to_a_real_run_with_no_key()
    {
        var item = new BulkCatalogueAddItem("c-1", "movie", "Dune", 2021, "tt1160419");
        var request = new BulkCatalogueAddRequest([item]);
        Assert.False(request.DryRun);
        Assert.Null(request.IdempotencyKey);
        Assert.Same(item, request.Items![0]);
        Assert.Null(new BulkCatalogueAddRequest(null, true, "key").Items);

        Assert.True(item.Monitored);
        Assert.True(item.IsReleased);
        Assert.Null(item.Episodes);
        Assert.Equal(("c-1", "movie", "Dune", 2021, "tt1160419", null), (item.ClientItemId, item.MediaType, item.Title, item.Year, item.ImdbId, item.LibraryId));
    }

    [Fact]
    public void A_catalogue_item_keeps_every_optional_field_it_is_given()
    {
        var air = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
        var episode = new BulkCatalogueEpisode(1, 2, "Two", "o", air, 12, 3, 4, "tvdb");
        var item = new BulkCatalogueAddItem(
            "c", "tv", "Show", 2020, "tt1", "lib", false, false, "tmdb", "99", "Original", "Overview", "poster", "backdrop",
            8.5, "Drama", "https://x", "{}", "anime", "absolute", "tvdb", [episode]);
        Assert.Equal(("lib", false, false, "tmdb", "99", "Original", "Overview", "poster", "backdrop", 8.5, "Drama", "https://x", "{}", "anime", "absolute", "tvdb"),
            (item.LibraryId, item.Monitored, item.IsReleased, item.MetadataProvider, item.MetadataProviderId, item.OriginalTitle, item.Overview, item.PosterUrl, item.BackdropUrl, item.Rating, item.Genres, item.ExternalUrl, item.MetadataJson, item.SeriesType, item.NumberingScheme, item.NumberingSource));
        Assert.Equal((1, 2, "Two", "o", air, 12, 3, 4, "tvdb"),
            (episode.SeasonNumber, episode.EpisodeNumber, episode.Title, episode.Overview, episode.AirDateUtc, episode.AbsoluteNumber, episode.SceneSeasonNumber, episode.SceneEpisodeNumber, episode.NumberingSource));
        Assert.Null(new BulkCatalogueEpisode(1, 1).Title);
    }

    [Fact]
    public void A_bulk_add_response_counts_what_became_of_each_item()
    {
        var result = new BulkCatalogueItemResult("c", "movie", "Dune", "created", "e-1", null, 0, 0, 0, "job-1");
        Assert.Equal(("c", "movie", "Dune", "created", "e-1", null, 0, 0, 0, "job-1"),
            (result.ClientItemId, result.MediaType, result.Title, result.Status, result.EntityId, result.Error, result.EpisodeCount, result.EpisodesAdded, result.EpisodesUpdated, result.RefreshJobId));
        var defaulted = new BulkCatalogueItemResult("c", "tv", null, "invalid");
        Assert.Equal((null, null, 0, null), (defaulted.EntityId, defaulted.Error, defaulted.EpisodeCount, defaulted.RefreshJobId));

        var response = new BulkCatalogueAddResponse(true, "key", 5, 1, 2, 1, 1, [result]);
        Assert.Equal((true, "key", 5, 1, 2, 1, 1), (response.DryRun, response.IdempotencyKey, response.Total, response.CreatedCount, response.ExistingCount, response.InvalidCount, response.FailedCount));
        Assert.Same(result, response.Items[0]);
    }

    [Fact]
    public void An_episode_request_for_a_known_show_keeps_its_items_and_results()
    {
        var air = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
        var item = new BulkSeriesEpisodeItem("c", 1, 2, "Two", "o", air, 12, 3, 4, "tvdb");
        var request = new BulkSeriesEpisodeRequest([item]);
        Assert.False(request.DryRun);
        Assert.Null(request.IdempotencyKey);
        Assert.Same(item, request.Episodes![0]);
        Assert.Equal(("c", 1, 2, "Two", "o", air, 12, 3, 4, "tvdb"),
            (item.ClientItemId, item.SeasonNumber, item.EpisodeNumber, item.Title, item.Overview, item.AirDateUtc, item.AbsoluteNumber, item.SceneSeasonNumber, item.SceneEpisodeNumber, item.NumberingSource));
        Assert.Null(new BulkSeriesEpisodeItem("c", 1, 1).Title);

        var episodeResult = new BulkSeriesEpisodeItemResult("c", 1, 2, "synced");
        Assert.Null(episodeResult.Error);
        Assert.Equal(("c", 1, 2, "synced"), (episodeResult.ClientItemId, episodeResult.SeasonNumber, episodeResult.EpisodeNumber, episodeResult.Status));
        Assert.Equal("bad", new BulkSeriesEpisodeItemResult("c", 1, 2, "invalid", "bad").Error);

        var response = new BulkSeriesEpisodeResponse(false, null, "s-1", 3, 2, 1, 0, 1, 1, [episodeResult]);
        Assert.Equal((false, null, "s-1", 3, 2, 1, 0, 1, 1),
            (response.DryRun, response.IdempotencyKey, response.SeriesId, response.Total, response.SyncedCount, response.InvalidCount, response.FailedCount, response.EpisodesAdded, response.EpisodesUpdated));
        Assert.Same(episodeResult, response.Episodes[0]);
    }

    [Fact]
    public void The_automation_summary_carries_readiness_queue_imports_and_what_needs_attention()
    {
        var now = DateTimeOffset.Parse("2026-10-31T00:00:00Z");
        var attention = new AutomationAttentionItem("code", "warn", "summary", "details", now);
        var summary = new AutomationSummaryResponse(
            now,
            new AutomationReadinessSummary("ready", true, 0),
            new AutomationQueueSummary(1, 2, 3, 4),
            new AutomationImportSummary(5, 6, 7, 8),
            [attention]);
        Assert.Equal(now, summary.GeneratedUtc);
        Assert.Equal(("ready", true, 0), (summary.Readiness.Status, summary.Readiness.Ready, summary.Readiness.FailedChecks));
        Assert.Equal((1, 2, 3, 4), (summary.Queue.Active, summary.Queue.Queued, summary.Queue.Failed, summary.Queue.OpenDispatchAlerts));
        Assert.Equal((5, 6, 7, 8), (summary.Imports.Active, summary.Imports.Failed, summary.Imports.Completed, summary.Imports.Issues));
        Assert.Equal(("code", "warn", "summary", "details", now), (attention.Code, attention.Severity, attention.Summary, attention.Details, attention.DetectedUtc));
    }
}
