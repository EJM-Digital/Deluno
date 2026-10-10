using System.Reflection;
using Deluno.Jobs.Data;
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
    /// Remove the old copy, not just delete it.
    ///
    /// <para>The distinction is the whole point of the override on a usenet
    /// client. Deleting removes the transfer; SABnzbd and NZBGet refuse a
    /// release from their <em>history</em>, which outlives the transfer — so a
    /// delete would report success and change nothing. On a torrent client the
    /// two resolve to the same request. The old copy and its files go (a forced
    /// re-download of the same release asks for that), and the line says so
    /// (#1241). This is the refused-copy removal, never "stop tracking".</para>
    /// </summary>
    [Fact]
    public async Task Clearing_a_download_removes_the_old_copy_and_says_so()
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

        Assert.Equal(DownloadClientActions.RemoveRefusedCopy, clients.RequestedAction);
        Assert.Equal("hash-1", clients.RequestedQueueItemId);
        Assert.Contains(result.Cleared, entry => entry.Contains("Removed the old copy", StringComparison.Ordinal));
    }

    /// <summary>
    /// The open dispatch is what says a client holds the release. Once the
    /// client has forgotten it, the dispatch is archived, so "already has a
    /// download" does not stay on the title (seen on the rig). A client that
    /// refuses leaves it alone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_forgotten_download_no_longer_counts_as_held(bool clientForgets)
    {
        var archived = new List<string>();
        var dispatches = DispatchProxy.Create<IDownloadDispatchesRepository, ArchiveRecorder>();
        ((ArchiveRecorder)(object)dispatches).Archived = archived;
        var clients = new FakeClients { Succeed = clientForgets, Message = "Not allowed." };
        var service = new AcquisitionOverrideService(new FakeProcessors(), new FakeExclusions(), clients, NullLogger<AcquisitionOverrideService>.Instance, dispatches);

        await service.ForceAsync(
            new AcquisitionOverrideRequest("movie-1", "Arrival", DownloadClientId: "client-1", DownloadClientName: "qBittorrent", QueueItemId: "hash-1", DispatchId: "dispatch-1"),
            CancellationToken.None);

        Assert.Equal(clientForgets ? ["dispatch-1"] : [], archived);
    }

    /// <summary>
    /// The blocker says Force un-refuses a blocked release; it now does, so the
    /// search it starts can use that release again (rig, #497).
    /// </summary>
    [Fact]
    public async Task A_force_un_refuses_the_titles_blocked_releases()
    {
        var unblocked = new List<string>();
        var blocked = DispatchProxy.Create<IBlockedReleaseRepository, UnblockRecorder>();
        ((UnblockRecorder)(object)blocked).Unblocked = unblocked;
        var service = new AcquisitionOverrideService(new FakeProcessors(), new FakeExclusions(), new FakeClients(), NullLogger<AcquisitionOverrideService>.Instance, null, blocked);

        var result = await service.ForceAsync(
            new AcquisitionOverrideRequest("movie-1", "The Terror", BlockedReleaseIdsOrNull: ["blocked-1"]),
            CancellationToken.None);

        Assert.Equal(["blocked-1"], unblocked);
        Assert.Contains("Un-refused the release, so searches can use it again.", result.Cleared);
    }

    public class UnblockRecorder : DispatchProxy
    {
        public List<string> Unblocked { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IBlockedReleaseRepository.UnblockAsync))
            {
                Unblocked.Add((string)args![0]!);
                return Task.FromResult(true);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class ArchiveRecorder : DispatchProxy
    {
        public List<string> Archived { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDownloadDispatchesRepository.ArchiveDispatchAsync))
            {
                Archived.Add((string)args![0]!);
                return Task.CompletedTask;
            }

            if (targetMethod?.Name == nameof(IDownloadDispatchesRepository.GetDispatchIncludingArchivedAsync))
            {
                // The override looks the dispatch up so the client service can
                // check it describes the item; this one holds none.
                return Task.FromResult<Deluno.Jobs.Contracts.DownloadDispatchItem?>(null);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
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

    /// <summary>
    /// The case the force exists for, which used to fall straight through.
    ///
    /// <para>The client still refuses the release because it remembers it, and
    /// the grab that would have given Deluno an id failed <em>because</em> the
    /// client already had it. Nothing in Deluno's own records carries the id,
    /// so the only place to get it is the client. Measured on the lab rig on
    /// 2026-09-05: the override reported "there was nothing to clear" about the
    /// very obstacle it had just been offered for, and the grab failed again
    /// for the same reason.</para>
    /// </summary>
    [Fact]
    public async Task A_force_with_no_id_asks_the_client_what_it_is_holding()
    {
        var service = Build(out _, out var clients);
        clients.Queue.Add(QueueItem("held-1", "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO"));

        var result = await service.ForceAsync(
            new AcquisitionOverrideRequest(
                "movie-1",
                "Big Buck Bunny",
                DownloadClientId: "client-1",
                DownloadClientName: "qBittorrent"),
            CancellationToken.None);

        Assert.Equal(DownloadClientActions.RemoveRefusedCopy, clients.RequestedAction);
        Assert.Equal("held-1", clients.RequestedQueueItemId);
        Assert.Contains(result.Cleared, entry => entry.Contains("Removed the old copy", StringComparison.Ordinal));
    }

    /// <summary>
    /// Forgetting the wrong download cannot be undone from inside Deluno, so an
    /// ambiguous answer is not acted on.
    /// </summary>
    [Fact]
    public async Task A_client_holding_two_matches_is_not_guessed_at()
    {
        var service = Build(out _, out var clients);
        clients.Queue.Add(QueueItem("held-1", "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO"));
        clients.Queue.Add(QueueItem("held-2", "Big.Buck.Bunny.2008.2160p.WEB-DL.x265-DELUNO"));

        var result = await service.ForceAsync(
            new AcquisitionOverrideRequest(
                "movie-1",
                "Big Buck Bunny",
                DownloadClientId: "client-1",
                DownloadClientName: "qBittorrent"),
            CancellationToken.None);

        Assert.Null(clients.RequestedAction);
        Assert.Contains(result.CouldNotClear, entry => entry.Contains("could not work out", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// And when the client holds nothing that matches, the force says it could
    /// not work out what to clear — never that there was nothing to clear.
    /// That distinction is the difference between "you are fine" and "I could
    /// not tell", and only one of them is true.
    /// </summary>
    [Fact]
    public async Task A_force_that_cannot_identify_the_item_says_so_rather_than_reporting_nothing_to_clear()
    {
        var service = Build(out _, out var clients);
        clients.Queue.Add(QueueItem("held-1", "Something.Else.2020.1080p"));

        var result = await service.ForceAsync(
            new AcquisitionOverrideRequest(
                "movie-1",
                "Big Buck Bunny",
                DownloadClientId: "client-1",
                DownloadClientName: "qBittorrent"),
            CancellationToken.None);

        Assert.NotEmpty(result.CouldNotClear);
        Assert.DoesNotContain("nothing to clear", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A client that cannot be reached is reported, not passed over.</summary>
    [Fact]
    public async Task A_client_that_will_not_answer_is_reported()
    {
        var service = Build(out _, out var clients);
        clients.OverviewThrows = true;

        var result = await service.ForceAsync(
            new AcquisitionOverrideRequest(
                "movie-1",
                "Big Buck Bunny",
                DownloadClientId: "client-1",
                DownloadClientName: "qBittorrent"),
            CancellationToken.None);

        Assert.NotEmpty(result.CouldNotClear);
    }

    private static DownloadQueueItem QueueItem(string id, string releaseName)
        => new(
            id,
            "client-1",
            "qBittorrent",
            "qbittorrent",
            "movies",
            releaseName,
            releaseName,
            "movies",
            "downloading",
            0.5,
            1.0,
            60,
            1000,
            500,
            10,
            "Lab Torznab",
            null,
            DateTimeOffset.UnixEpoch);

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
        public Task RecordProcessorObservationAsync(string id, ProcessorHandoffObservation observation, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ProcessorHandoffItem? Handoff { get; set; }
        public string? UpdatedStatus { get; private set; }
        public string? UpdatedOutputPath { get; private set; }
        public string? UpdatedImportJobId { get; private set; }

        public Task<bool> RepointWaitingProcessorHandoffAsync(string id, string sourcePath, CancellationToken cancellationToken) => throw new NotSupportedException();

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
        public Task<ProcessorHandoffItem?> FindProcessorHandoffForDownloadAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsForReleaseAsync(string releaseName, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsAsync(string? libraryId, int take, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsWithStatusAsync(IReadOnlyList<string> statuses, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcessorConnectionItem>> ListProcessorConnectionsAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorConnectionItem?> GetProcessorConnectionAsync(string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorConnectionItem?> FindProcessorConnectionByNameAsync(string? name, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorConnectionItem?> FindProcessorConnectionAsync(string? id, string? nameFallback, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorConnectionItem> CreateProcessorConnectionAsync(CreateProcessorConnectionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorConnectionItem?> UpdateProcessorConnectionAsync(string id, UpdateProcessorConnectionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<bool> DeleteProcessorConnectionAsync(string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorConnectionItem?> RecordProcessorConnectionHealthAsync(string id, string status, string? message, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcessorPathMappingItem>> ListProcessorPathMappingsAsync(string processorConnectionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProcessorPathMappingItem>>([]);
        public Task<ProcessorPathMappingItem?> CreateProcessorPathMappingAsync(string processorConnectionId, CreateProcessorPathMappingRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<bool> DeleteProcessorPathMappingAsync(string processorConnectionId, string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ProcessorOutcomeRecord?> GetProcessorOutcomeAsync(string handoffId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task SaveProcessorOutcomeAsync(ProcessorOutcomeRecord record, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcessorOutcomeRecord>> ListDueProcessorOutcomesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcessorOutcomeRecord>> ListProcessorOutcomesAsync(IReadOnlyCollection<string> handoffIds, CancellationToken cancellationToken)
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

        /// <summary>What the client says it is holding when Deluno asks.</summary>
        public List<DownloadQueueItem> Queue { get; } = [];
        public bool OverviewThrows { get; set; }

        public Task<IReadOnlyList<DownloadClientTelemetrySnapshot>> GetSnapshotsForLeftoverScanAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken)
        {
            if (OverviewThrows)
            {
                throw new HttpRequestException("The client is not answering.");
            }

            var summary = new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
            var snapshot = new DownloadClientTelemetrySnapshot(
                "client-1",
                "qBittorrent",
                "qbittorrent",
                null,
                "healthy",
                null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "lab"),
                summary,
                Queue,
                [],
                DateTimeOffset.UnixEpoch);

            return Task.FromResult(new DownloadTelemetryOverview(summary, [snapshot], DateTimeOffset.UnixEpoch));
        }
        public Task<DownloadCleanupPreview?> PreviewCleanupAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<DownloadHealthRemediationReport> RunConfiguredHealthRemediationAsync(DownloadTelemetryOverview overview, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<DownloadClientActionResult> ReclaimCompletedAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<QueueRemovalResult> RemoveWithReasonAsync(string clientId, string queueItemId, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<bool> ClearClientWarningsAsync(string clientId, CancellationToken cancellationToken)
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
