using System.Text.Json;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

/// <summary>
/// The dispatch alerts behind an import-recovery case.
///
/// <para>The poller records one problem twice: a recovery case, which the
/// Import issues list shows and a person dismisses, and a dispatch alert, which
/// no screen shows and monitoring counts. Settling the case left the alert open
/// until the download was archived weeks later (#537). A case settled by a
/// person now settles the alerts on its download too.</para>
/// </summary>
public static class RecoveryCaseAlerts
{
    /// <summary>Acknowledges the open alerts on the case's download; returns how many.</summary>
    /// <param name="detailsJson">
    /// The case's details: <c>DispatchId</c>, or for cases recorded before it was
    /// kept, <c>DownloadClientId</c> and <c>ReleaseName</c> - which cannot say
    /// which of a release's grabs it was, so the alerts on all of them close.
    /// </param>
    /// <param name="dispatches">
    /// When given, the case's download is settled too if it failed. Transfers
    /// lists a failed download under Needs attention beside the import issue
    /// about it; they are one problem, and a person who has dealt with it once
    /// should not find it waiting again under another name.
    /// </param>
    public static async Task<int> AcknowledgeAsync(
        string? detailsJson,
        IDispatchAlertRepository alertRepository,
        CancellationToken cancellationToken,
        IDownloadDispatchesRepository? dispatches = null)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            if (Text(root, "DispatchId") is { } dispatchId)
            {
                if (dispatches is not null
                    && await dispatches.GetDispatchAsync(dispatchId, cancellationToken) is { } dispatch
                    && (string.Equals(dispatch.Status, "failed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(dispatch.ImportStatus, "failed", StringComparison.OrdinalIgnoreCase)))
                {
                    await dispatches.ArchiveDispatchAsync(dispatchId, "issue_settled", cancellationToken);
                }

                return await alertRepository.AcknowledgeAlertsForDispatchAsync(dispatchId, cancellationToken);
            }

            return Text(root, "DownloadClientId") is { } clientId && Text(root, "ReleaseName") is { } releaseName
                ? await alertRepository.AcknowledgeAlertsForReleaseAsync(clientId, releaseName, cancellationToken)
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.EnumerateObject()
            .Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            .Select(property => property.Value.GetString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
