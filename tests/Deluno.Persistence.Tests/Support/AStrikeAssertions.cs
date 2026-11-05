using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Worker.Jobs;

namespace Deluno.Persistence.Tests.Support;

/// <summary>What must be true once a download has struck out under a "replace it" rule, whichever client it came from.</summary>
internal static class AStrikeAssertions
{
    /// <summary>
    /// The release is refused with the rule's own reason, the client was asked to remove it together with its data, its dispatch is
    /// archived, Activity says why in plain words, and a replacement search is queued for the very dispatch it replaces.
    /// </summary>
    public static async Task ReplacedAsync(AStrikePass world, IReadOnlyList<(string Action, string ItemId)> actions, string says)
    {
        Assert.Equal([(DownloadClientActions.DeleteWithData, world.QueueItemId)], actions);

        var refused = Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Equal(BlockedReleaseStates.Refused, refused.State);
        Assert.Equal(world.ReleaseName, refused.ReleaseName);
        Assert.Equal(AStrikePass.EntityId, refused.EntityId);
        Assert.StartsWith(says, refused.Reason, StringComparison.Ordinal);

        // The dispatch is closed, so the title no longer reads "downloading".
        var dispatch = await world.Dispatches.GetDispatchIncludingArchivedAsync(world.DispatchId, CancellationToken.None);
        Assert.Equal("archived", dispatch!.Status);
        Assert.Empty(await world.Jobs.ListDownloadsInFlightAsync(100, CancellationToken.None));

        // Activity says why, in plain words, and what Deluno did about it.
        var activity = Assert.Single(
            await world.Jobs.ListActivityAsync(20, null, null, CancellationToken.None),
            item => item.Category == "download.health.remediated");
        Assert.StartsWith(world.ReleaseName, activity.Message, StringComparison.Ordinal);
        Assert.Contains(says, activity.Message, StringComparison.Ordinal);
        Assert.Contains("blocked this release", activity.Message);
        Assert.Contains("is looking for another copy", activity.Message);
        Assert.Contains($"removed it from {world.Client.Name}", activity.Message);

        await ReplacementQueuedAsync(world);
    }

    /// <summary>The replacement search is for this title, is linked to the dispatch it replaces, and ignores that dispatch (#1482).</summary>
    public static async Task ReplacementQueuedAsync(AStrikePass world)
    {
        var job = Assert.Single(await world.JobsQueuedAsync());
        Assert.Equal(LibrarySearchJobTypes.For("movies"), job.JobType);
        Assert.Equal("download_dispatch", job.RelatedEntityType);
        Assert.Equal(world.DispatchId, job.RelatedEntityId);
        Assert.Equal(world.DispatchId, SearchExecutionSupport.ReplacedDispatchId(job));
        using var payload = JsonDocument.Parse(job.PayloadJson!);
        Assert.Equal("health-replacement", payload.RootElement.GetProperty("triggeredBy").GetString());
        Assert.True(SearchExecutionSupport.IsReplacementSearch("health-replacement"));
        Assert.Equal(AStrikePass.EntityId, payload.RootElement.GetProperty("targetEntityId").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("maxItems").GetInt32());

        // When the search was queued the replaced download was still in the client and its dispatch still open: the search can
        // start before the removal and the archive (#1482). It must not count that download as on its way, or it would send
        // nothing; with the old behaviour (not ignoring it) it would have been held back by it.
        var inFlight = Assert.Single(world.Watcher.InFlightWhenQueued!);
        Assert.Equal(world.DispatchId, inFlight.DispatchId);
        Assert.Null(world.Watcher.HeldBackIgnoringTheReplacedDownload);
        Assert.NotNull(world.Watcher.HeldBackNotIgnoringIt);
    }
}
