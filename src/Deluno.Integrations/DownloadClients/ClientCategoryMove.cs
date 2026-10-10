namespace Deluno.Integrations.DownloadClients;

/// <summary>What a client said when asked about one torrent's category (#1240).</summary>
/// <param name="Has">Whether the client has the torrent at all.</param>
/// <param name="Category">The category or label it reports, when it has the torrent; blank for none.</param>
internal readonly record struct ClientCategoryReading(ClientHas Has, string? Category);

/// <summary>
/// One rule for every torrent client that moves a download to a keep-seeding category (#1240): the move counts as
/// done only when the client had the torrent before the call and reports it in the new category after. It is the
/// same "look, don't believe" as <see cref="ClientRemovalGuard"/>: a client's "yes" says nothing about what it did.
/// Nothing here removes, pauses or relocates anything; the only change a caller makes is in <c>set</c>.
/// </summary>
internal static class ClientCategoryMove
{
    public static bool SameCategory(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static async Task<DownloadClientActionResult> RunAsync(
        Deluno.Connections.Contracts.DownloadClientItem client,
        string queueItemId,
        string category,
        string clientName,
        Func<Task<ClientCategoryReading>> read,
        Func<ClientCategoryReading, Task<DownloadClientActionResult>> set)
    {
        var action = DownloadClientActions.SetCategory;
        var before = await read();
        switch (before.Has)
        {
            case ClientHas.No:
                return DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                    $"{clientName} has no download with that id, so nothing was moved. It may already have been removed there.");
            case ClientHas.Unknown:
                return DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                    $"{clientName} could not be asked about this download, so nothing was changed.", category: "unreachable");
        }

        // Already there (a retry after a move that worked but was not seen): nothing to ask the client to do.
        if (SameCategory(before.Category, category))
        {
            return DownloadClientHelpers.ActionSuccess(client, queueItemId, action, $"{clientName} already has this download in {category}.");
        }

        var result = await set(before);
        if (!result.Succeeded)
        {
            return result;
        }

        var after = await read();
        return after.Has switch
        {
            ClientHas.Yes when SameCategory(after.Category, category) =>
                DownloadClientHelpers.ActionSuccess(client, queueItemId, action, $"{clientName} moved this download to {category}."),
            ClientHas.Yes => DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                $"{clientName} still has this download in {(string.IsNullOrWhiteSpace(after.Category) ? "no category" : after.Category)} after being told to move it to {category}, so it was not moved."),
            ClientHas.No => DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                $"{clientName} no longer lists this download, so Deluno cannot say it was moved."),
            _ => DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                $"{clientName} did not confirm the move, so Deluno cannot say it happened.", category: "unreachable")
        };
    }
}
