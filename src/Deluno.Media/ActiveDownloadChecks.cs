using Deluno.Integrations.DownloadClients;

namespace Deluno.Media;

/// <summary>The hand-send checks every kind of title shares (#1175).</summary>
public static class ActiveDownloadChecks
{
    /// <summary>A film (or any whole title): anything of its own already on its way counts.</summary>
    public static ActiveDownloadCheck ForWholeTitle(string entityId, string title, IDownloadClientTelemetryService telemetry)
        => new(
            title,
            download => string.Equals(download.EntityId, entityId, StringComparison.OrdinalIgnoreCase),
            (conflict, cancellationToken) => ReplaceAsync(telemetry, conflict, cancellationToken));

    /// <summary>Replace: removes the first download from its client, files and all, and blocklists nothing. Null when done, else why not.</summary>
    public static async Task<string?> ReplaceAsync(
        IDownloadClientTelemetryService telemetry,
        ActiveDownloadConflict conflict,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conflict.ClientId) || string.IsNullOrWhiteSpace(conflict.QueueItemId))
        {
            return $"Deluno cannot tell which download in {conflict.ClientName} to remove. Remove it there, or send this one as well.";
        }

        var removed = await telemetry.RemoveWithOptionsAsync(
            conflict.ClientId, conflict.QueueItemId!, removeFromClient: true, blocklist: false, skipRedownload: true, cancellationToken);
        return removed.Succeeded ? null : $"The first download could not be removed from {conflict.ClientName}: {removed.Message}";
    }
}
