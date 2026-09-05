using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Media;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// Forcing a re-download clears records, and reports exactly which.
///
/// <para>The behaviour being replaced is Radarr's: a release that failed is
/// blocklisted so it "will not be automatically downloaded ever again", and it
/// stays that way "forever unless you manually remove them". The mechanism is
/// right — without it a failed import loops for ever — and it is silent, so a
/// person meets a title that never arrives and no account of why.</para>
///
/// <para>What is asserted here is the honesty rather than the clearing. A force
/// touches a download client and a processor, neither of which is Deluno's, and
/// none of it is undone by pressing the button again. So it has to say what it
/// did, and it has to say what it could not do rather than quietly reporting
/// success.</para>
/// </summary>
public sealed class AcquisitionOverrideServiceTests
{
    [Fact]
    public async Task A_force_with_nothing_to_clear_says_so_rather_than_claiming_success()
    {
        var service = Build(out _, out _);

        var result = await service.ForceAsync(
            new AcquisitionOverrideRequest("movie-1", "Arrival"),
            CancellationToken.None);

        Assert.Empty(result.Cleared);
        Assert.Empty(result.CouldNotClear);
        Assert.Contains("nothing to clear", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The hand-off goes back to waiting rather than being deleted: the row is
    /// what stops one download being submitted to the processor twice, and
    /// losing it would trade this problem for that one.
    /// </summary>
    [Fact]
    public async Task Clearing_a_handoff_resets_it_to_waiting_rather_than_deleting_it()
    {
        var service = Build(out var processors, out _);
        processors.Handoff = Handoff("handoff-1", "completed", "Weir");

        var result = await service.ForceAsync(
            new AcquisitionOverrideRequest("movie-1", "Arrival", HandoffId: "handoff-1"),
            CancellationToken.None);

        Assert.Equal("waiting", processors.UpdatedStatus);
        Assert.Null(processors.UpdatedOutputPath);
        Assert.Null(processors.UpdatedImportJobId);
        Assert.Contains(result.Cleared, entry => entry.Contains("Weir", StringComparison.Ordinal));
    }

    /// <summary>
    /// With its data. The reason to force is that what is there is unwanted, and
    /// leaving the files would have the client refuse the same release again for
    /// the same reason.
    /// </summary>
    [Fact]
    public async Task Clearing_a_download_removes_it_with_its_files()
    {
        var service = Build(out _, out var clients);

        var result = await service.ForceAsync(
            new AcquisitionOverrideRequest(
                "movie-1",
                "Arrival",
                DownloadClientId: "client-1",
                DownloadClientName: "qBittorrent",
                QueueItemId: "hash-1"),
            CancellationToken.None);

        Assert.Equal("delete-with-data", clients.RequestedAction);
        Assert.Equal("hash-1", clients.RequestedQueueItemId);
        Assert.Contains(result.Cleared, entry => entry.Contains("qBittorrent", StringComparison.Ordinal));
    }

    /// <summary>
    /// A client that refuses is reported, not swallowed — and its own words are
    /// carried through, because "it would not remove it" without the reason is
    /// no more use than the silence being replaced.
    /// </summary>
    [Fact]
    public async Task A_client_that_refuses_is_reported_in_its_own_words()
    {
        var service = Build(out _, out var clients);
        clients.Succeed = false;
        clients.Message = "Torrent is locked by another process.";

        var result = await service.ForceAsync(
            new AcquisitionOverrideRequest(
                "movie-1",
                "Arrival",
                DownloadClientId: "client-1",
                DownloadClientName: "qBittorrent",
                QueueItemId: "hash-1"),
            CancellationToken.None);

        Assert.Empty(result.Cleared);
        Assert.Contains(result.CouldNotClear, entry => entry.Contains("locked by another process", StringComparison.Ordinal));
    }

    /// <summary>
    /// And a client that cannot be reached at all does not take the rest of the
    /// force down with it. Three of four, with the fourth named, beats a
    /// rollback that leaves the person where they started knowing nothing.
    /// </summary>
    [Fact]
    public async Task One_step_failing_does_not_abandon_the_others()
    {
        var service = Build(out var processors, out var clients);
        processors.Handoff = Handoff("handoff-1", "completed", "Weir");
        clients.Throw = true;

        var result = await service.ForceAsync(
            new AcquisitionOverrideRequest(
                "movie-1",
                "Arrival",
                HandoffId: "handoff-1",
                DownloadClientId: "client-1",
                DownloadClientName: "qBittorrent",
                QueueItemId: "hash-1"),
            CancellationToken.None);

        // The hand-off was still worth resetting.
        Assert.Single(result.Cleared);
        Assert.Single(result.CouldNotClear);
        Assert.Contains("Cleared 1 of 2", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handoff_that_is_no_longer_there_is_reported_rather_than_claimed()
    {
        var service = Build(out var processors, out _);
        processors.Handoff = null;

        var result = await service.ForceAsync(
            new AcquisitionOverrideRequest("movie-1", "Arrival", HandoffId: "gone"),
            CancellationToken.None);

        Assert.Empty(result.Cleared);
        Assert.Contains(result.CouldNotClear, entry => entry.Contains("could not be found", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ helpers

    private static AcquisitionOverrideService Build(out FakeProcessors processors, out FakeClients clients)
    {
        processors = new FakeProcessors();
        clients = new FakeClients();
        return new AcquisitionOverrideService(
            processors,
            new FakeExclusions(),
            clients,
            NullLogger<AcquisitionOverrideService>.Instance);
    }

    private static ProcessorHandoffItem Handoff(string id, string status, string processorName)
        => new(
            id,
            "library-1",
            "movies",
            "client-1",
            "queue-1",
            "Arrival.2016.1080p",
            "/downloads/arrival",
            processorName,
            status,
            "/refined/arrival.mkv",
            "import-1",
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private sealed class FakeProcessors : IProcessorRepository
    {
        public ProcessorHandoffItem? Handoff { get; set; }
        public string? UpdatedStatus { get; private set; }
        public string? UpdatedOutputPath { get; private set; }
        public string? UpdatedImportJobId { get; private set; }

        public Task<ProcessorHandoffItem?> UpdateProcessorHandoffAsync(
            string id, string status, string? outputPath, string? importJobId, string? failureMessage, CancellationToken cancellationToken)
        {
            UpdatedStatus = status;
            UpdatedOutputPath = outputPath;
            UpdatedImportJobId = importJobId;
            return Task.FromResult(Handoff is null ? null : Handoff with { Status = status });
        }

        public Task<ProcessorHandoffItem> EnsureProcessorHandoffAsync(CreateProcessorHandoffRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorHandoffItem?> FindProcessorHandoffAsync(string libraryId, string? handoffId, string? sourcePath, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorHandoffItem?> GetProcessorHandoffAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult(Handoff);
        public Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsAsync(string? libraryId, int take, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcessorConnectionItem>> ListProcessorConnectionsAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorConnectionItem?> GetProcessorConnectionAsync(string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorConnectionItem?> FindProcessorConnectionByNameAsync(string? name, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorConnectionItem> CreateProcessorConnectionAsync(CreateProcessorConnectionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorConnectionItem?> UpdateProcessorConnectionAsync(string id, UpdateProcessorConnectionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<bool> DeleteProcessorConnectionAsync(string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorConnectionItem?> RecordProcessorConnectionHealthAsync(string id, string status, string? message, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeClients : IDownloadClientTelemetryService
    {
        public bool Succeed { get; set; } = true;
        public bool Throw { get; set; }
        public string Message { get; set; } = "Removed.";
        public string? RequestedAction { get; private set; }
        public string? RequestedQueueItemId { get; private set; }

        public Task<DownloadClientActionResult> ExecuteActionAsync(string clientId, DownloadClientActionRequest request, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new HttpRequestException("The client is not answering.");
            }

            RequestedAction = request.Action;
            RequestedQueueItemId = request.QueueItemId;
            return Task.FromResult(new DownloadClientActionResult(clientId, request.QueueItemId, request.Action, Succeed, Message));
        }

        public Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<DownloadCleanupPreview?> PreviewCleanupAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<DownloadHealthRemediationReport> RunConfiguredHealthRemediationAsync(DownloadTelemetryOverview overview, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<DownloadClientActionResult> ReclaimCompletedAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeExclusions : IUnifiedExclusionRepository
    {
        public Task<IReadOnlyList<MediaExclusionItem>> ListActiveAsync(string? mediaType, string? sourceKind, string? sourceId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MediaExclusionItem>>([]);
        public Task<MediaExclusionItem?> UpsertAsync(UpsertMediaExclusionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult(true);
        public Task<bool> DeleteByScopeAsync(string sourceKind, string sourceId, string entryKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
