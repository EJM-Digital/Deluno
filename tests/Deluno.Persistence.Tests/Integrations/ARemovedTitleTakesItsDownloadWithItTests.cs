using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Cleanup sheet J3/L1/L2 (#899, #902, #903): whether a title is deleted by
/// hand, taken by a retention rule, or removed while its download is still
/// active at the client or mid-processing, it goes through the one code path
/// - <see cref="TitleRemovalService.RemoveAsync"/>, and specifically
/// <c>CancelActiveDownloadsAsync</c> inside it - and that path always cancels
/// the download with its data, discards whatever a processor is doing with
/// it, drops its subtitles, says so in Activity in plain words, and never
/// blocklists the release: the title was not bad, it just is not wanted any
/// more.
///
/// <para>Drives the real service against real SQLite repositories (dispatches,
/// activity/jobs, processor hand-offs, subtitles, the blocklist itself) with a
/// fake download-client telemetry service and a fake processor connection, the
/// same shape <see cref="ARemovalReasonDoesExactlyWhatItSaysTests"/> and
/// <see cref="AForeignDownloadIsNeverStruckOrRemediatedTests"/> use.</para>
/// </summary>
public sealed class ARemovedTitleTakesItsDownloadWithItTests
{
    [Fact]
    public async Task Deleting_a_title_by_hand_cancels_its_active_download_with_its_data_and_never_blocklists()
    {
        using var world = await World.CreateAsync();
        var movie = await world.ImportMovieAsync("Arrival");
        world.Dispatch(movie.Id, "Arrival.2016.1080p.WEB-DL-GROUP", "client-1", "hash-arrival");

        var metadata = await world.RemoveMovieAsync(movie, new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: false));

        var call = Assert.Single(world.Telemetry.Calls);
        Assert.Equal("client-1", call.ClientId);
        Assert.Equal(DownloadClientActions.DeleteWithData, call.Action);
        Assert.Equal("hash-arrival", call.QueueItemId);
        Assert.Equal("1", metadata["cancelledClientDownloadCount"]);

        // Never blocklisted, whatever the removal reason - the class has no
        // path to the blocklist at all, and this is that fact proven live.
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "movie.removed");
        Assert.Contains("Arrival was removed from Deluno", activity.Message);
    }

    [Fact]
    public async Task A_download_for_a_different_title_is_left_running()
    {
        using var world = await World.CreateAsync();
        var removed = await world.ImportMovieAsync("Arrival");
        var other = await world.ImportMovieAsync("Dune");
        world.Dispatch(removed.Id, "Arrival.2016.1080p.WEB-DL-GROUP", "client-1", "hash-arrival");
        var otherDispatchId = world.Dispatch(other.Id, "Dune.2021.2160p.WEB-DL-GROUP", "client-1", "hash-dune");

        await world.RemoveMovieAsync(removed, new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: false));

        var call = Assert.Single(world.Telemetry.Calls);
        Assert.Equal("hash-arrival", call.QueueItemId);

        var untouched = await world.Dispatches.GetDispatchAsync(otherDispatchId, CancellationToken.None);
        Assert.NotNull(untouched);
        Assert.NotEqual("archived", untouched!.Status, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #933: a dispatch Deluno recorded but never saw the client take (no
    /// torrent hash or item id recorded against it) names nothing at the
    /// client to cancel, so it is skipped rather than guessed at.
    /// </summary>
    [Fact]
    public async Task A_dispatch_the_client_never_confirmed_is_never_asked_to_cancel_anything()
    {
        using var world = await World.CreateAsync();
        var movie = await world.ImportMovieAsync("Arrival");
        world.DispatchWithoutDetection(movie.Id, "Arrival.2016.1080p.WEB-DL-GROUP", "client-1");

        var metadata = await world.RemoveMovieAsync(movie, new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: false));

        Assert.Empty(world.Telemetry.Calls);
        Assert.Equal("0", metadata["cancelledClientDownloadCount"]);
    }

    [Fact]
    public async Task A_retention_removal_cancels_the_download_too_and_the_activity_carries_the_rules_reason()
    {
        using var world = await World.CreateAsync();
        var movie = await world.ImportMovieAsync("Arrival");
        world.Dispatch(movie.Id, "Arrival.2016.1080p.WEB-DL-GROUP", "client-1", "hash-arrival");

        var reason = "the library's retention rule removed it 30 days after it was watched";
        var metadata = await world.RemoveMovieAsync(
            movie,
            new TitleRemovalOptions(DeleteFiles: true, AddImportListExclusion: false, Reason: reason));

        Assert.Equal("1", metadata["cancelledClientDownloadCount"]);
        Assert.Single(world.Telemetry.Calls);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "movie.removed");
        Assert.Equal($"Arrival was removed from Deluno: {reason}", activity.Message);
    }

    /// <summary>
    /// A title removed while a processor is already mid-run on its download:
    /// the processor's output is discarded rather than waited for, even
    /// though it already started work - #903's whole point.
    /// </summary>
    [Fact]
    public async Task A_title_removed_mid_processing_discards_the_processors_output_even_though_it_already_started()
    {
        using var world = await World.CreateAsync();
        var movie = await world.ImportMovieAsync("Arrival");
        const string release = "Arrival.2016.1080p.WEB-DL-GROUP";
        world.Dispatch(movie.Id, release, "client-1", "hash-arrival");
        var handoffId = await world.HandOffToProcessorAsync(release, ProcessorHandoffStatuses.Started);
        world.ProcessorConnections.NextCancelResult = new ProcessorHandoffCancelResult(ProcessorAnswer.Busy, "Remuxing.");

        var metadata = await world.RemoveMovieAsync(movie, new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: false));

        // The active download is still cancelled at the client...
        Assert.Equal("1", metadata["cancelledClientDownloadCount"]);
        // ...and the hand-off is dismissed, its output discarded, despite the
        // processor saying it had already started work on it.
        Assert.Equal("1", metadata["stoppedProcessorHandoffCount"]);
        var handoff = await world.Processors.GetProcessorHandoffAsync(handoffId, CancellationToken.None);
        Assert.NotNull(handoff);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, handoff!.Status);
        Assert.Contains("already started work", handoff.FailureMessage);
        Assert.Contains("output will not be imported", handoff.FailureMessage);
        Assert.Single(world.ProcessorConnections.Calls, call => call.HandoffId == handoffId);
    }

    /// <summary>
    /// A hand-off still waiting on a title Deluno is NOT removing, and one
    /// already ended, are not this removal's to touch.
    /// </summary>
    [Fact]
    public async Task Only_the_removed_titles_own_open_hand_off_is_ended()
    {
        using var world = await World.CreateAsync();
        var removed = await world.ImportMovieAsync("Arrival");
        const string release = "Arrival.2016.1080p.WEB-DL-GROUP";
        world.Dispatch(removed.Id, release, "client-1", "hash-arrival");
        var openHandoffId = await world.HandOffToProcessorAsync(release, ProcessorHandoffStatuses.Submitted);

        var other = await world.ImportMovieAsync("Dune");
        const string otherRelease = "Dune.2021.2160p.WEB-DL-GROUP";
        world.Dispatch(other.Id, otherRelease, "client-1", "hash-dune");
        var otherHandoffId = await world.HandOffToProcessorAsync(otherRelease, ProcessorHandoffStatuses.Submitted);

        await world.RemoveMovieAsync(removed, new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: false));

        var ended = await world.Processors.GetProcessorHandoffAsync(openHandoffId, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, ended!.Status);

        var untouched = await world.Processors.GetProcessorHandoffAsync(otherHandoffId, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Submitted, untouched!.Status);
    }

    /// <summary>
    /// Deleting a title's files takes its subtitles with them (#893's rule
    /// again), not just the video they belong to.
    /// </summary>
    [Fact]
    public async Task Deleting_a_titles_files_takes_its_subtitles_with_them()
    {
        using var world = await World.CreateAsync();
        var movie = await world.ImportMovieAsync("Arrival", filePath: @"D:\Media\Arrival (2016)\Arrival (2016).mkv");
        await world.Subtitles.RecordFetchedAsync(
            MediaKind.Movie,
            movie.Id,
            new MediaSubtitleRow("en", SubtitleSources.Fetched, false, false, @"D:\Media\Arrival (2016)\Arrival (2016).en.srt", null, "srt", "gestdown"),
            CancellationToken.None);

        await world.RemoveMovieAsync(movie, new TitleRemovalOptions(DeleteFiles: true, AddImportListExclusion: false));

        var moved = Assert.Single(world.RecycleBin.MoveCalls);
        Assert.Contains(moved.TrackedFiles, file => file.FilePath == @"D:\Media\Arrival (2016)\Arrival (2016).mkv");
        Assert.Contains(moved.TrackedFiles, file => file.FilePath == @"D:\Media\Arrival (2016)\Arrival (2016).en.srt");
    }

    /// <summary>
    /// The same shared service, exercised for a TV show rather than a film -
    /// movies and TV stay separate catalogues, but removal is one code path
    /// for both (#899/#902/#903 apply equally).
    /// </summary>
    [Fact]
    public async Task A_series_removal_cancels_its_active_download_the_same_way()
    {
        using var world = await World.CreateAsync();
        var series = await world.ImportSeriesAsync("Severance");
        world.Dispatch(series.Id, "Severance.S01E01.1080p.WEB-DL-GROUP", "client-1", "hash-severance", entityType: "series");

        var metadata = await world.RemoveSeriesAsync(series, new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: false));

        var call = Assert.Single(world.Telemetry.Calls);
        Assert.Equal("hash-severance", call.QueueItemId);
        Assert.Equal("1", metadata["cancelledClientDownloadCount"]);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "series.removed");
        Assert.Contains("Severance was removed from Deluno", activity.Message);
    }

    /// <summary>
    /// #900: unmonitoring a title mid-download is not just a flag - the same
    /// cancel-with-data path removal uses runs for it too, without removing
    /// the title itself.
    /// </summary>
    [Fact]
    public async Task Unmonitoring_a_title_mid_download_cancels_it_with_its_data_and_never_blocklists()
    {
        using var world = await World.CreateAsync();
        var movie = await world.ImportMovieAsync("Arrival");
        world.Dispatch(movie.Id, "Arrival.2016.1080p.WEB-DL-GROUP", "client-1", "hash-arrival");

        var metadata = await world.UnmonitorAsync("movie", movie.Id, "Arrival");

        var call = Assert.Single(world.Telemetry.Calls);
        Assert.Equal("client-1", call.ClientId);
        Assert.Equal(DownloadClientActions.DeleteWithData, call.Action);
        Assert.Equal("hash-arrival", call.QueueItemId);
        Assert.Equal("1", metadata["cancelledClientDownloadCount"]);

        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "movie.download-cancelled");
        Assert.Contains("Arrival is no longer monitored", activity.Message);
        Assert.Contains("cancelled its download", activity.Message);
    }

    /// <summary>Unmonitoring an upgrade in progress cancels the upgrade download, but there is no library file for this test to check is untouched, so it is proven at the endpoint layer that the existing file is never referenced by this path.</summary>
    [Fact]
    public async Task Unmonitoring_a_title_with_nothing_in_flight_reports_nothing_cancelled_and_writes_no_activity()
    {
        using var world = await World.CreateAsync();
        var movie = await world.ImportMovieAsync("Arrival");

        var metadata = await world.UnmonitorAsync("movie", movie.Id, "Arrival");

        Assert.Equal("0", metadata["cancelledClientDownloadCount"]);
        Assert.Empty(world.Telemetry.Calls);
        Assert.DoesNotContain(
            await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None),
            entry => entry.Category == "movie.download-cancelled");
    }

    /// <summary>The TV side of #900, same as series removal reuses the same code (#899/#902/#903).</summary>
    [Fact]
    public async Task Unmonitoring_a_series_mid_download_cancels_it_the_same_way()
    {
        using var world = await World.CreateAsync();
        var series = await world.ImportSeriesAsync("Severance");
        world.Dispatch(series.Id, "Severance.S01E01.1080p.WEB-DL-GROUP", "client-1", "hash-severance", entityType: "series");

        var metadata = await world.UnmonitorAsync("series", series.Id, "Severance");

        var call = Assert.Single(world.Telemetry.Calls);
        Assert.Equal("hash-severance", call.QueueItemId);
        Assert.Equal("1", metadata["cancelledClientDownloadCount"]);

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "series.download-cancelled");
        Assert.Contains("Severance is no longer monitored", activity.Message);
    }

    /// <summary>A download for a title you did not unmonitor is left running, same as removal leaves other titles alone.</summary>
    [Fact]
    public async Task A_download_for_a_different_title_is_left_running_when_unmonitoring()
    {
        using var world = await World.CreateAsync();
        var unmonitored = await world.ImportMovieAsync("Arrival");
        var other = await world.ImportMovieAsync("Dune");
        world.Dispatch(unmonitored.Id, "Arrival.2016.1080p.WEB-DL-GROUP", "client-1", "hash-arrival");
        var otherDispatchId = world.Dispatch(other.Id, "Dune.2021.2160p.WEB-DL-GROUP", "client-1", "hash-dune");

        await world.UnmonitorAsync("movie", unmonitored.Id, "Arrival");

        var call = Assert.Single(world.Telemetry.Calls);
        Assert.Equal("hash-arrival", call.QueueItemId);

        var untouched = await world.Dispatches.GetDispatchAsync(otherDispatchId, CancellationToken.None);
        Assert.NotNull(untouched);
        Assert.NotEqual("archived", untouched!.Status, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A processor already mid-run on the cancelled download's release has its
    /// output discarded rather than waited for, matching #903's rule for
    /// removal.
    /// </summary>
    [Fact]
    public async Task Unmonitoring_mid_processing_discards_the_processors_output_even_though_it_already_started()
    {
        using var world = await World.CreateAsync();
        var movie = await world.ImportMovieAsync("Arrival");
        const string release = "Arrival.2016.1080p.WEB-DL-GROUP";
        world.Dispatch(movie.Id, release, "client-1", "hash-arrival");
        var handoffId = await world.HandOffToProcessorAsync(release, ProcessorHandoffStatuses.Started);
        world.ProcessorConnections.NextCancelResult = new ProcessorHandoffCancelResult(ProcessorAnswer.Busy, "Remuxing.");

        var metadata = await world.UnmonitorAsync("movie", movie.Id, "Arrival");

        Assert.Equal("1", metadata["cancelledClientDownloadCount"]);
        Assert.Equal("1", metadata["stoppedProcessorHandoffCount"]);
        var handoff = await world.Processors.GetProcessorHandoffAsync(handoffId, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, handoff!.Status);
    }

    /// <summary>
    /// #947: the most common TV grab is an automatic per-episode search, and
    /// it dispatches under the episode's own id, not the series' - removing
    /// or unmonitoring a series has to reach those dispatches too.
    /// </summary>
    [Fact]
    public async Task A_series_removal_also_cancels_dispatches_recorded_under_its_episodes()
    {
        using var world = await World.CreateAsync();
        var (severance, episodeIds) = await world.ImportSeriesWithEpisodesAsync("Severance", episodeCount: 2);
        world.Dispatch(episodeIds[0], "Severance.S01E01.1080p.WEB-DL-GROUP", "client-1", "hash-s01e01", entityType: "episode");

        var metadata = await world.RemoveSeriesAsync(severance, new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: false));

        var call = Assert.Single(world.Telemetry.Calls);
        Assert.Equal("hash-s01e01", call.QueueItemId);
        Assert.Equal("1", metadata["cancelledClientDownloadCount"]);

        var dispatch = (await world.Dispatches.QueryDispatchesAsync(
            new DispatchQueryFilter { EntityType = "episode", EntityId = episodeIds[0], IncludeArchived = true },
            new DispatchPaginationOptions { PageSize = 10 },
            CancellationToken.None)).Items.Single();
        Assert.Equal("archived", dispatch.Status, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The unmonitor-a-series side of the same gap (#900's path, #947's fix).</summary>
    [Fact]
    public async Task Unmonitoring_a_series_also_cancels_dispatches_recorded_under_its_episodes()
    {
        using var world = await World.CreateAsync();
        var (severance, episodeIds) = await world.ImportSeriesWithEpisodesAsync("Severance", episodeCount: 2);
        world.Dispatch(episodeIds[1], "Severance.S01E02.1080p.WEB-DL-GROUP", "client-1", "hash-s01e02", entityType: "episode");

        var metadata = await world.UnmonitorSeriesAsync(severance.Id, "Severance", episodeIds);

        var call = Assert.Single(world.Telemetry.Calls);
        Assert.Equal("hash-s01e02", call.QueueItemId);
        Assert.Equal("1", metadata["cancelledClientDownloadCount"]);
    }

    /// <summary>
    /// #947: unmonitoring the last still-monitored episode a season pack
    /// covers also cancels, archives and dismisses the hand-off for the pack
    /// itself.
    /// </summary>
    [Fact]
    public async Task Unmonitoring_the_last_monitored_episode_a_season_pack_covers_cancels_the_pack_too()
    {
        using var world = await World.CreateAsync();
        var (severance, episodeIds) = await world.ImportSeriesWithEpisodesAsync("Severance", episodeCount: 2);
        world.DispatchSeasonPack(severance.Id, "Severance.S01.1080p.WEB-DL-GROUP", "client-1", "hash-s01-pack", episodeIds);
        var handoffId = await world.HandOffToProcessorAsync("Severance.S01.1080p.WEB-DL-GROUP", ProcessorHandoffStatuses.Submitted);

        var seasonPack = new TitleRemovalService.SeasonPackUnmonitorContext(severance.Id, (_, _) => Task.FromResult(true));
        var metadata = await world.UnmonitorEpisodeAsync(episodeIds[0], "An episode of Severance", seasonPack);

        var call = Assert.Single(world.Telemetry.Calls);
        Assert.Equal("hash-s01-pack", call.QueueItemId);
        Assert.Equal("1", metadata["cancelledClientDownloadCount"]);
        Assert.Equal("1", metadata["archivedGrabCount"]);

        var handoff = await world.Processors.GetProcessorHandoffAsync(handoffId, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, handoff!.Status);
    }

    /// <summary>
    /// #947: unmonitoring one episode of a two-episode season pack, while the
    /// other episode it covers is still monitored, leaves the pack running -
    /// Activity says so rather than the person losing track of why.
    /// </summary>
    [Fact]
    public async Task Unmonitoring_one_episode_a_season_pack_covers_leaves_it_running_while_another_stays_monitored()
    {
        using var world = await World.CreateAsync();
        var (severance, episodeIds) = await world.ImportSeriesWithEpisodesAsync("Severance", episodeCount: 2);
        world.DispatchSeasonPack(severance.Id, "Severance.S01.1080p.WEB-DL-GROUP", "client-1", "hash-s01-pack", episodeIds);

        var seasonPack = new TitleRemovalService.SeasonPackUnmonitorContext(severance.Id, (_, _) => Task.FromResult(false));
        var metadata = await world.UnmonitorEpisodeAsync(episodeIds[0], "An episode of Severance", seasonPack);

        Assert.Empty(world.Telemetry.Calls);
        Assert.Equal("0", metadata["cancelledClientDownloadCount"]);

        var packDispatchId = (await world.Dispatches.QueryDispatchesAsync(
            new DispatchQueryFilter { EntityType = "series", EntityId = severance.Id, IncludeArchived = true },
            new DispatchPaginationOptions { PageSize = 10 },
            CancellationToken.None)).Items.Single().Id;
        var pack = await world.Dispatches.GetDispatchAsync(packDispatchId, CancellationToken.None);
        Assert.NotEqual("archived", pack!.Status, StringComparer.OrdinalIgnoreCase);

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "series.season-pack-kept-running");
        Assert.Contains("still monitored", activity.Message);
    }

    private sealed class FakeDownloadClientTelemetryService : IDownloadClientTelemetryService
    {
        public List<(string ClientId, string Action, string QueueItemId)> Calls { get; } = [];

        public bool NextResultSucceeds { get; set; } = true;

        public Task<DownloadClientActionResult> ExecuteActionAsync(string clientId, DownloadClientActionRequest request, CancellationToken cancellationToken)
        {
            Calls.Add((clientId, request.Action, request.QueueItemId));
            return Task.FromResult(new DownloadClientActionResult(clientId, request.QueueItemId, request.Action, NextResultSucceeds, "ok"));
        }

        public Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientTelemetrySnapshot>> GetSnapshotsForLeftoverScanAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadCleanupPreview?> PreviewCleanupAsync(string clientId, string queueItemId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadHealthRemediationReport> RunConfiguredHealthRemediationAsync(DownloadTelemetryOverview overview, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ReclaimCompletedAsync(string clientId, string queueItemId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QueueRemovalResult> RemoveWithReasonAsync(string clientId, string queueItemId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ClearClientWarningsAsync(string clientId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeProcessorConnectionService : IProcessorConnectionService
    {
        public List<(ProcessorConnectionItem Connection, string HandoffId)> Calls { get; } = [];

        public ProcessorHandoffCancelResult NextCancelResult { get; set; } = new(ProcessorAnswer.Answered, "Dropped.");

        public Task<ProcessorHandoffCancelResult> CancelHandoffAsync(ProcessorConnectionItem connection, string handoffId, CancellationToken cancellationToken)
        {
            Calls.Add((connection, handoffId));
            return Task.FromResult(NextCancelResult);
        }

        public Task<ProcessorConnectionTestResult> TestAsync(ProcessorConnectionItem connection, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProcessorSubmissionResult> SubmitAsync(ProcessorConnectionItem connection, ProcessorHandoffItem handoff, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProcessorStatusSupport> GetStatusSupportAsync(ProcessorConnectionItem connection, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProcessorHandoffStatusResult> GetHandoffStatusAsync(ProcessorConnectionItem connection, string handoffId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProcessorOutcomeResult> ReportOutcomeAsync(ProcessorConnectionItem connection, string handoffId, ProcessorHandoffOutcome outcome, string? importedPath, string? reason, DateTimeOffset occurredUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Never called - <see cref="TitleRemovalOptions.AddImportListExclusion"/> stays false throughout.</summary>
    private sealed class UnusedIntakeRepository : IIntakeRepository
    {
        public Task<IReadOnlyList<IntakeSourceItem>> ListIntakeSourcesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IntakeSourceItem?> GetIntakeSourceAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<IntakeListExclusionItem>> ListActiveIntakeListExclusionsAsync(string sourceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IntakeListExclusionItem?> CreateIntakeListExclusionAsync(string sourceId, CreateIntakeListExclusionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteIntakeListExclusionAsync(string sourceId, string exclusionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<IntakeTitleOriginItem>> ListIntakeTitleOriginsAsync(string mediaType, string entityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IntakeTitleOriginItem?> RecordIntakeTitleOriginAsync(CreateIntakeTitleOriginRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<IntakeDroppedTitle>> MarkDroppedTitlesAsync(string sourceId, DateTimeOffset syncStartedUtc, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IntakeSourceItem> CreateIntakeSourceAsync(CreateIntakeSourceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IntakeSourceItem?> UpdateIntakeSourceAsync(string id, UpdateIntakeSourceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IntakeSourceItem?> RecordIntakeSourceSyncResultAsync(string id, DateTimeOffset syncedUtc, string status, string? summary, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteIntakeSourceAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeRecycleBinService : IRecycleBinService
    {
        public List<(IReadOnlyList<TrackedLibraryFile> TrackedFiles, IReadOnlyList<LibraryItem> Libraries)> MoveCalls { get; } = [];

        public Task<RecycleBinMoveResult> MoveAsync(IReadOnlyList<TrackedLibraryFile> trackedFiles, IReadOnlyList<LibraryItem> libraries, CancellationToken cancellationToken)
        {
            MoveCalls.Add((trackedFiles, libraries));
            return Task.FromResult(new RecycleBinMoveResult(trackedFiles.Count, 0, [], []));
        }

        public Task<RecycleBinSettings> GetSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RecycleBinSettings> SaveSettingsAsync(RecycleBinSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RecycleBinItem>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RecycleBinItem?> StoreReplacementAsync(LibraryItem library, string originalPath, string existingPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RecycleBinItem?> StoreRemovedFileAsync(LibraryItem library, string path, RecycleBinFileOwner? owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RecycleBinItem?> StoreDownloadFileAsync(LibraryItem library, string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AttachTitleAsync(IReadOnlyList<string> itemIds, RecycleBinTitleSnapshot title, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReleaseTitleAsync(string snapshotId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RecycleBinOperationResult> RestoreAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RecycleBinOperationResult> PermanentlyDeleteAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CleanupAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RecycleBinCleanupPreview> PreviewCleanupAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage storage;
        private readonly SqliteMovieCatalogRepository movies;
        private readonly SqliteSeriesCatalogRepository series;
        private readonly TitleRemovalService removal;

        private World(
            TestStorage storage,
            SqliteMovieCatalogRepository movies,
            SqliteSeriesCatalogRepository series,
            SqliteLibrariesRepository libraries,
            SqliteJobStore jobs,
            SqliteDownloadDispatchesRepository dispatches,
            SqliteBlockedReleaseRepository blocklist,
            SqliteProcessorRepository processors,
            SqliteMediaSubtitleRepository subtitles,
            FakeDownloadClientTelemetryService telemetry,
            FakeProcessorConnectionService processorConnections,
            FakeRecycleBinService recycleBin,
            LibraryItem library)
        {
            this.storage = storage;
            this.movies = movies;
            this.series = series;
            Dispatches = dispatches;
            Blocklist = blocklist;
            Processors = processors;
            Subtitles = subtitles;
            Telemetry = telemetry;
            ProcessorConnections = processorConnections;
            RecycleBin = recycleBin;
            Activity = jobs;
            Library = library;

            removal = new TitleRemovalService(
                libraries,
                new UnusedIntakeRepository(),
                jobs,
                jobs,
                recycleBin,
                dispatches,
                processors,
                processorConnections,
                mediaServerJobScheduler: null,
                downloadClientTelemetry: telemetry,
                subtitleRepository: subtitles);
        }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        public SqliteProcessorRepository Processors { get; }

        public SqliteMediaSubtitleRepository Subtitles { get; }

        public FakeDownloadClientTelemetryService Telemetry { get; }

        public FakeProcessorConnectionService ProcessorConnections { get; }

        public FakeRecycleBinService RecycleBin { get; }

        public IActivityFeedRepository Activity { get; }

        public LibraryItem Library { get; }

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new SeriesSchemaInitializer(storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var library = await libraries.CreateLibraryAsync(
                new CreateLibraryRequest(
                    "Library", "movies", "library", Path.Combine(storage.DataRoot, "media"), Path.Combine(storage.DataRoot, "downloads"), null,
                    "direct-import", null, null, null, null, true, true, true, 12, 24, 25),
                CancellationToken.None);

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var subtitles = new SqliteMediaSubtitleRepository(storage.Factory, time);
            var moviesRepository = new SqliteMovieCatalogRepository(storage.Factory, time);
            var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, time);

            var weir = await processors.CreateProcessorConnectionAsync(
                new CreateProcessorConnectionRequest("Weir", "weir", "http://weir.local/submit", null, null, IsEnabled: true),
                CancellationToken.None);
            await LegacyConnectionNames.SetAsync(storage.Factory, "processor_connections", weir.Id, "Weir");

            return new World(
                storage,
                moviesRepository,
                seriesRepository,
                libraries,
                jobs,
                dispatches,
                blocklist,
                processors,
                subtitles,
                new FakeDownloadClientTelemetryService(),
                new FakeProcessorConnectionService(),
                new FakeRecycleBinService(),
                library);
        }

        public async Task<MovieListItem> ImportMovieAsync(string title, string? filePath = null)
        {
            await movies.ImportExistingBatchAsync(
                Library.Id,
                [
                    new ExistingMovieImportRequest(
                        Title: title,
                        ReleaseYear: 2016,
                        WantedStatus: WantedStatuses.Covered,
                        WantedReason: "Imported from your existing library.",
                        CurrentQuality: "WEB 1080p",
                        TargetQuality: "WEB 1080p",
                        QualityCutoffMet: true,
                        FilePath: filePath ?? $@"D:\Media\{title}\{title}.mkv",
                        FileSizeBytes: 4_000_000_000)
                ],
                CancellationToken.None);

            var page = await movies.ListPageAsync(new CatalogueQuery(Search: title), CancellationToken.None);
            return page.Items.Single(item => item.Title == title);
        }

        public async Task<SeriesListItem> ImportSeriesAsync(string title)
        {
            await series.ImportExistingBatchAsync(
                Library.Id,
                [
                    new ExistingSeriesImportRequest(
                        Title: title,
                        StartYear: 2022,
                        WantedStatus: WantedStatuses.Covered,
                        WantedReason: "Imported from your existing library.",
                        CurrentQuality: null,
                        TargetQuality: null,
                        QualityCutoffMet: false,
                        FilePath: null,
                        FileSizeBytes: null,
                        Episodes: null)
                ],
                CancellationToken.None);

            var page = await series.ListPageAsync(new CatalogueQuery(Search: title), CancellationToken.None);
            return page.Items.Single(item => item.Title == title);
        }

        /// <summary>
        /// A show with real episode rows, so <c>SeriesRemoval.Target</c>'s
        /// <c>RelatedEpisodeIds</c> (#947) has something to read back from
        /// <c>GetInventoryDetailAsync</c>, and season-pack dispatches have
        /// real episode ids to name in their notes.
        /// </summary>
        public async Task<(SeriesListItem Series, IReadOnlyList<string> EpisodeIds)> ImportSeriesWithEpisodesAsync(string title, int episodeCount)
        {
            var episodes = Enumerable.Range(1, episodeCount)
                .Select(number => new ImportedEpisodeItem(SeasonNumber: 1, EpisodeNumber: number, HasFile: false))
                .ToArray();

            await series.ImportExistingBatchAsync(
                Library.Id,
                [
                    new ExistingSeriesImportRequest(
                        Title: title,
                        StartYear: 2022,
                        WantedStatus: WantedStatuses.Covered,
                        WantedReason: "Imported from your existing library.",
                        CurrentQuality: null,
                        TargetQuality: null,
                        QualityCutoffMet: false,
                        FilePath: null,
                        FileSizeBytes: null,
                        Episodes: episodes)
                ],
                CancellationToken.None);

            var page = await series.ListPageAsync(new CatalogueQuery(Search: title), CancellationToken.None);
            var seriesItem = page.Items.Single(item => item.Title == title);
            var inventory = await series.GetInventoryDetailAsync(seriesItem.Id, CancellationToken.None);
            var episodeIds = inventory!.Episodes.OrderBy(episode => episode.EpisodeNumber).Select(episode => episode.EpisodeId).ToArray();
            return (seriesItem, episodeIds);
        }

        public string Dispatch(string entityId, string releaseName, string clientId, string torrentHashOrItemId, string entityType = "movie")
        {
            var dispatchId = DispatchRecord(entityId, releaseName, clientId, entityType);
            Dispatches.RecordDetectionAsync(dispatchId, torrentHashOrItemId, downloadedBytes: 1024, completed: false, CancellationToken.None)
                .GetAwaiter().GetResult();
            return dispatchId;
        }

        /// <summary>A season-pack dispatch, recorded under the series' own id with the
        /// episode ids it covers in its notes, the same shape SeasonSearchExecutor writes.</summary>
        public string DispatchSeasonPack(string seriesId, string releaseName, string clientId, string torrentHashOrItemId, IReadOnlyList<string> episodeIds)
        {
            var notesJson = JsonSerializer.Serialize(new { episodeIds });
            var dispatchId = ((SqliteJobStore)Activity).RecordDownloadDispatchAsync(
                Library.Id, "tv", "series", seriesId, releaseName, "TestIndexer", clientId, "Test Client", "sent", notesJson,
                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            Dispatches.RecordDetectionAsync(dispatchId, torrentHashOrItemId, downloadedBytes: 1024, completed: false, CancellationToken.None)
                .GetAwaiter().GetResult();
            return dispatchId;
        }

        public void DispatchWithoutDetection(string entityId, string releaseName, string clientId, string entityType = "movie")
            => DispatchRecord(entityId, releaseName, clientId, entityType);

        private string DispatchRecord(string entityId, string releaseName, string clientId, string entityType)
        {
            var mediaType = entityType == "series" ? "tv" : "movies";
            return ((SqliteJobStore)Activity).RecordDownloadDispatchAsync(
                Library.Id, mediaType, entityType, entityId, releaseName, "TestIndexer", clientId, "Test Client", "sent", null,
                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task<string> HandOffToProcessorAsync(string releaseName, string status)
        {
            var handoff = await Processors.EnsureProcessorHandoffAsync(
                new CreateProcessorHandoffRequest(Library.Id, "movies", "client-1", "queue-item-1", releaseName, "/downloads/" + releaseName, "Weir"),
                CancellationToken.None);
            await Processors.UpdateProcessorHandoffAsync(handoff.Id, status, null, null, null, CancellationToken.None);
            return handoff.Id;
        }

        public Task<Dictionary<string, string?>> RemoveMovieAsync(MovieListItem movie, TitleRemovalOptions options)
            => removal.RemoveAsync(MovieRemoval.Target(movie, movies), options, CancellationToken.None);

        public Task<Dictionary<string, string?>> RemoveSeriesAsync(SeriesListItem seriesItem, TitleRemovalOptions options)
            => removal.RemoveAsync(SeriesRemoval.Target(seriesItem, series), options, CancellationToken.None);

        public Task<Dictionary<string, string?>> UnmonitorAsync(string entityType, string entityId, string title)
            => removal.CancelDownloadForUnmonitoredTitleAsync(entityType, entityId, title, CancellationToken.None);

        public Task<Dictionary<string, string?>> UnmonitorSeriesAsync(string seriesId, string title, IReadOnlyList<string> relatedEpisodeIds)
            => removal.CancelDownloadForUnmonitoredTitleAsync("series", seriesId, title, CancellationToken.None, relatedEpisodeIds: relatedEpisodeIds);

        public Task<Dictionary<string, string?>> UnmonitorEpisodeAsync(string episodeId, string title, TitleRemovalService.SeasonPackUnmonitorContext seasonPack)
            => removal.CancelDownloadForUnmonitoredTitleAsync("episode", episodeId, title, CancellationToken.None, seasonPack: seasonPack);

        public void Dispose() => storage.Dispose();
    }
}
