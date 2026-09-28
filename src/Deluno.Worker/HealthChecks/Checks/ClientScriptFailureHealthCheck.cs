using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// The download itself finished, but the client's own post-processing script
/// failed or warned (#910) -- NZBGet's FAILURE/SCRIPT and WARNING/SCRIPT,
/// SABnzbd's <c>fail_msg</c>. <see cref="Services.DownloadClientTelemetryService"/>
/// already records this as a <see cref="DownloadHealthStrikes.ClientScriptFailed"/>
/// finding and says it in Activity; this check is the second place it is
/// seen (#952) -- System &#8594; Health, so it stays visible after the
/// activity feed scrolls past it, until the client lists the download again
/// without the finding (the script ran cleanly, or it was cleared) or a
/// person snoozes the row from the screen.
///
/// <para>Not a strike: <see cref="DownloadHealthStrikes.Counts"/> deliberately
/// excludes this kind, so nothing here ever blocklists the release, removes
/// the download or searches for a replacement -- the finished file is left
/// for the ordinary import path.</para>
/// </summary>
public sealed class ClientScriptFailureHealthCheck(
    IDownloadHealthRepository downloadHealth,
    IConnectionsRepository connections) : IHealthCheck
{
    public string Category => HealthCheckCategories.DownloadClient;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var records = await downloadHealth.ListDownloadHealthRecordsAsync(200, cancellationToken);
        var findings = records
            .Where(record => string.Equals(record.Kind, DownloadHealthStrikes.ClientScriptFailed, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (findings.Length == 0)
        {
            return [];
        }

        var clients = await connections.ListDownloadClientsAsync(cancellationToken);
        var clientNames = clients.ToDictionary(client => client.Id, client => client.Name, StringComparer.Ordinal);

        return findings.Select(record =>
        {
            var clientName = clientNames.GetValueOrDefault(record.ClientId, "Your download client");
            var isFailure = string.Equals(record.Severity, "critical", StringComparison.OrdinalIgnoreCase);

            return HealthCheckOutcome.Problem(
                $"client-script-failed:{record.ClientId}:{record.QueueItemId}",
                Category,
                $"{record.ReleaseName} (client script {(isFailure ? "failed" : "warned")})",
                $"{record.ReleaseName}: {record.Evidence}",
                cause: $"{clientName}'s own post-processing script did not run cleanly on this download -- a problem with the script, not the release.",
                fix: $"Check the post-processing script in {clientName}'s settings. Deluno still imports the finished file once it is complete and playable.",
                detailsJson: $$"""{"clientId":"{{record.ClientId}}","queueItemId":"{{record.QueueItemId}}"}""");
        }).ToArray();
    }
}
