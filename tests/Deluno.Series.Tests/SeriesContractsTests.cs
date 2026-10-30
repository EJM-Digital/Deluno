using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Moq;

namespace Deluno.Series.Tests;

/// <summary>
/// The plain request/response records of the series API, and the do-nothing defaults a repository
/// inherits for the repair operations only the SQLite repository implements.
/// </summary>
public sealed class SeriesContractsTests
{
    [Fact]
    public void A_bulk_request_carries_its_values_and_defaults()
    {
        var plain = new BulkSeriesRequest(["a", "b"], "search");
        Assert.Equal(["a", "b"], plain.SeriesIds);
        Assert.Equal("search", plain.Operation);
        Assert.Null(plain.QualityProfileId);
        Assert.Null(plain.Monitored);
        Assert.Null(plain.DownloadClientId);
        Assert.Null(plain.ForceOverrideReason);
        Assert.False(plain.DeleteFiles);
        Assert.False(plain.AddImportListExclusion);

        var full = new BulkSeriesRequest(["a"], "grab", "q", true, "c", "why", true, true);
        Assert.Equal(("q", true, "c", "why", true, true),
            (full.QualityProfileId, full.Monitored, full.DownloadClientId, full.ForceOverrideReason, full.DeleteFiles, full.AddImportListExclusion));
    }

    [Fact]
    public void A_bulk_response_carries_its_counts_and_results()
    {
        var item = new BulkSeriesItemResult("s-1", "Show", false, "no", new Dictionary<string, string?> { ["k"] = "v" });
        var response = new BulkSeriesResponse(1, 0, 1, "remove", [item]);
        Assert.Equal((1, 0, 1, "remove"), (response.TotalProcessed, response.SuccessCount, response.FailureCount, response.Operation));
        Assert.Same(item, response.Results[0]);
        Assert.Equal(("s-1", "Show", false, "no"), (item.SeriesId, item.SeriesTitle, item.Succeeded, item.ErrorMessage));
        Assert.Equal("v", item.Metadata!["k"]);
        Assert.Null(new BulkSeriesItemResult("s", "t", true).ErrorMessage);
        Assert.Null(new BulkSeriesItemResult("s", "t", true).Metadata);
    }

    [Fact]
    public void A_workflow_decision_defaults_to_nothing_and_keeps_what_it_is_given()
    {
        var empty = new EpisodeWorkflowDecision();
        Assert.Equal(("", ""), (empty.EpisodeId, empty.Decision));
        Assert.False(empty.IsReplacementAllowed);
        Assert.Null(empty.QualityDelta);

        var full = new EpisodeWorkflowDecision("e", "wanted", "r", "missing", true, 2, "720p", "1080p");
        Assert.Equal(("e", "wanted", "r", "missing", true, 2, "720p", "1080p"),
            (full.EpisodeId, full.Decision, full.Reason, full.WantedStatus, full.IsReplacementAllowed, full.QualityDelta, full.CurrentQuality, full.TargetQuality));
    }

    [Fact]
    public void A_monitoring_request_keeps_its_ids_and_flag()
    {
        var request = new UpdateSeriesMonitoringRequest(["a"], true);
        Assert.Equal(["a"], request.SeriesIds);
        Assert.True(request.Monitored);
        Assert.Null(new UpdateSeriesMonitoringRequest(null, false).SeriesIds);
    }

    [Fact]
    public async Task A_repository_without_the_repair_operations_does_nothing_and_says_so()
    {
        ISeriesCatalogRepository repository = new Mock<ISeriesCatalogRepository> { CallBase = true }.Object;
        Assert.Empty(await repository.RepairWrongEpisodeFileLinksAsync(CancellationToken.None));
        Assert.Empty(await repository.ListEpisodeFileLinksAsync(CancellationToken.None));
        Assert.False(await repository.ClearMislabelledEpisodeFileAsync("e", "f", CancellationToken.None));
        Assert.Equal(0, await repository.DetachSeriesFileRecordsAsync("s", "f", CancellationToken.None));
        Assert.Equal(0, await repository.ClearSeriesFileRecordsInRecycleBinAsync(CancellationToken.None));
        Assert.Empty(await repository.RelinkEpisodesWithTheirOwnFilesAsync(CancellationToken.None));
        Assert.Empty(await repository.SettleImportRecoveryCasesListedAsync(new Deluno.Contracts.ImportRecoveryCaseScope([], []), "r", CancellationToken.None));
    }
}
