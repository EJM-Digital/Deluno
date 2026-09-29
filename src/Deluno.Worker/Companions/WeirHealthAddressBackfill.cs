using Deluno.Companions;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Worker.Companions;

/// <summary>
/// #1055: the processor connection the Weir companion made before connections could carry a
/// health-check address has none, so its test could only ever read "degraded". This gives that
/// one connection Weir's published <c>/ready</c>, once, and tests it.
///
/// <para>The marker is the companion's own record: the login it saved for Weir holds the base
/// address it connected to. Only a connection with no health-check address whose hand-off
/// address is on that base address, on Weir's intake path, is touched. Every other processor is
/// left alone.</para>
/// </summary>
public sealed class WeirHealthAddressBackfill(
    ICompanionLoginStore logins,
    IProcessorRepository processors,
    IProcessorConnectionService processorConnections,
    IActivityFeedRepository activity)
{
    public const string ActivityCategory = "processor.health-address.added";

    private const string IntakePrefix = "/api/v1/intake/webhook/";

    /// <summary>Returns how many connections were updated (0 or 1 in practice).</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var login = await logins.GetAsync(CompanionToolIds.Weir, cancellationToken);
        if (login is null || string.IsNullOrWhiteSpace(login.Url))
        {
            return 0;
        }

        var baseUrl = login.Url.TrimEnd('/');
        var updated = 0;
        foreach (var connection in await processors.ListProcessorConnectionsAsync(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(connection.HealthCheckUrl)
                || !connection.SubmissionUrl.StartsWith(baseUrl + IntakePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var changed = await processors.UpdateProcessorConnectionAsync(
                connection.Id,
                new UpdateProcessorConnectionRequest(null, null, null, null, null, connection.IsEnabled, baseUrl + WeirCompanion.WeirReadyPath),
                cancellationToken);
            if (changed is null)
            {
                continue;
            }

            var test = await processorConnections.TestAsync(changed, cancellationToken);
            await processors.RecordProcessorConnectionHealthAsync(changed.Id, test.Status, test.Message, cancellationToken);
            await activity.RecordActivityAsync(
                ActivityCategory,
                $"{connection.Name} now has a health-check address ({baseUrl}{WeirCompanion.WeirReadyPath}), so Deluno can tell whether it is up. Its test now says: {test.Message}",
                null, null, "processor-connection", connection.Id, cancellationToken);
            updated++;
        }

        return updated;
    }
}
