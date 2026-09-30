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
/// <para>The marker is the connection itself: Weir's hand-off address is on this computer
/// (loopback) and on Weir's intake path, and the connection has no health-check address. Weir's
/// base address is that hand-off address up to the intake path. Every other processor is left
/// alone. Deluno keeps no Weir login any more (#1061), so nothing else is needed.</para>
/// </summary>
public sealed class WeirHealthAddressBackfill(
    IProcessorRepository processors,
    IProcessorConnectionService processorConnections,
    IActivityFeedRepository activity)
{
    public const string ActivityCategory = "processor.health-address.added";

    private const string IntakePrefix = "/api/v1/intake/webhook/";

    /// <summary>Returns how many connections were updated (0 or 1 in practice).</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var updated = 0;
        foreach (var connection in await processors.ListProcessorConnectionsAsync(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(connection.HealthCheckUrl) || BaseAddressOnThisComputer(connection.SubmissionUrl) is not { } baseUrl)
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

    /// <summary>Weir's base address when <paramref name="submissionUrl"/> is on Weir's intake path on this computer; otherwise null.</summary>
    internal static string? BaseAddressOnThisComputer(string? submissionUrl)
    {
        if (string.IsNullOrWhiteSpace(submissionUrl) || !Uri.TryCreate(submissionUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback
            || !uri.AbsolutePath.StartsWith(IntakePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"http://{uri.Host}:{uri.Port}";
    }
}
