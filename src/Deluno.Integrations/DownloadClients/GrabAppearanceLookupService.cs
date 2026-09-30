using Deluno.Connections.Data;
using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// The #911 lookup itself: given the client a release was sent to, its hash
/// or item id and its name, works out why it never appeared.
///
/// <para>Reads the same snapshot and history every client adapter already
/// exposes for Queue and the telemetry drawer (<see cref="IDownloadClient.GetSnapshotAsync"/>,
/// <see cref="IDownloadClient.GetHistoryAsync"/>) — both already cover every
/// category the client holds, not only the one this grab asked for, so no
/// adapter needed a new call to answer this.</para>
/// </summary>
public sealed class GrabAppearanceLookupService(
    IConnectionsRepository connectionsRepository,
    IDownloadClientRegistry downloadClientRegistry,
    TimeProvider timeProvider) : IGrabAppearanceLookupService
{
    public async Task<GrabAppearanceLookupResult> LookupAsync(
        string downloadClientId,
        string mediaType,
        string? category,
        string? torrentHashOrItemId,
        string releaseName,
        CancellationToken cancellationToken)
    {
        var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, downloadClientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return new GrabAppearanceLookupResult(
                GrabAppearanceOutcome.Unreachable,
                Detail: "That download client no longer exists.");
        }

        var expectedCategory = ExpectedCategory(client, mediaType, category);

        if (!client.IsEnabled || !downloadClientRegistry.TryGet(client.Protocol, out var implementation))
        {
            return new GrabAppearanceLookupResult(
                GrabAppearanceOutcome.Unreachable,
                ExpectedCategory: expectedCategory,
                Detail: !client.IsEnabled
                    ? $"{client.Name} is disabled."
                    : $"'{client.Protocol}' is not a supported download client protocol.");
        }

        DownloadClientTelemetrySnapshot? snapshot;
        IReadOnlyList<DownloadClientHistoryItem> history;
        try
        {
            var capturedUtc = timeProvider.GetUtcNow();
            snapshot = await implementation.GetSnapshotAsync(client, capturedUtc, cancellationToken);
            history = snapshot is null
                ? []
                : await implementation.GetHistoryAsync(client, capturedUtc, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return new GrabAppearanceLookupResult(
                GrabAppearanceOutcome.Unreachable,
                ExpectedCategory: expectedCategory,
                Detail: exception.Message);
        }

        // A configuration problem (missing address, bad credentials) answers
        // with an empty, failed snapshot rather than throwing. Read the same
        // way as an exception: nothing here says anything about the release.
        if (snapshot is null || snapshot.LastFailure is not null)
        {
            return new GrabAppearanceLookupResult(
                GrabAppearanceOutcome.Unreachable,
                ExpectedCategory: expectedCategory,
                Detail: snapshot?.LastHealthMessage);
        }

        // By the id Deluno recorded for this grab, and only by that: a release
        // name is not proof that an item is the one Deluno sent (a same-named
        // item may have been added outside Deluno, and Deluno never adopts
        // one by name - James, 30 Sep 2026).
        bool Matches(string? id)
            => !string.IsNullOrWhiteSpace(torrentHashOrItemId) && string.Equals(id?.Trim(), torrentHashOrItemId.Trim(), StringComparison.OrdinalIgnoreCase);

        // The live queue first — it is the current truth, and a torrent
        // client's history is often only what the queue itself implies.
        var queueMatch = snapshot.Queue.FirstOrDefault(item => Matches(item.Id));
        if (queueMatch is not null)
        {
            var foundElsewhere = !string.IsNullOrWhiteSpace(expectedCategory)
                && !string.Equals(queueMatch.Category, expectedCategory, StringComparison.OrdinalIgnoreCase);
            return new GrabAppearanceLookupResult(
                foundElsewhere ? GrabAppearanceOutcome.FoundUnderAnotherCategory : GrabAppearanceOutcome.FoundAsExpected,
                FoundCategory: queueMatch.Category,
                ExpectedCategory: expectedCategory,
                QueueItemId: queueMatch.Id);
        }

        var historyMatch = history.FirstOrDefault(item => Matches(item.ExternalId));
        if (historyMatch is not null)
        {
            if (string.Equals(historyMatch.Outcome, DownloadClientHistoryOutcomes.Duplicate, StringComparison.OrdinalIgnoreCase))
            {
                return new GrabAppearanceLookupResult(
                    GrabAppearanceOutcome.Duplicate,
                    FoundCategory: historyMatch.Category,
                    ExpectedCategory: expectedCategory,
                    QueueItemId: historyMatch.ExternalId ?? historyMatch.Id,
                    Detail: historyMatch.ErrorMessage);
            }

            // Only a client that answered directly, not this codebase's own
            // "no error text but no progress either" guess (queue-derived
            // history), counts as the client rejecting the file.
            if (string.Equals(historyMatch.Outcome, "failed", StringComparison.OrdinalIgnoreCase)
                && string.Equals(historyMatch.HistorySource, "native", StringComparison.OrdinalIgnoreCase))
            {
                return new GrabAppearanceLookupResult(
                    GrabAppearanceOutcome.Rejected,
                    FoundCategory: historyMatch.Category,
                    ExpectedCategory: expectedCategory,
                    Detail: historyMatch.ErrorMessage);
            }
        }

        return new GrabAppearanceLookupResult(GrabAppearanceOutcome.NotThere, ExpectedCategory: expectedCategory);
    }

    private static string? ExpectedCategory(
        Deluno.Connections.Contracts.DownloadClientItem client,
        string mediaType,
        string? category)
        => !string.IsNullOrWhiteSpace(category)
            ? category
            : string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
                ? client.TvCategory ?? client.CategoryTemplate
                : client.MoviesCategory ?? client.CategoryTemplate;
}
