using Deluno.Contracts;
using Deluno.Integrations.Metadata;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// TMDb and the other configured metadata sources, reachable (#909). Before
/// this, metadata was only ever probed when a person opened Settings and
/// pressed Test -- an install left running unattended could lose TMDb for
/// days with nothing to say so. This asks the provider to actually reach out
/// (<see cref="IMetadataProvider.CheckReachabilityAsync"/>), not just report
/// its last incidental result.
///
/// <para>Metadata that has never been configured is a setup step, not a
/// problem, so it reports nothing rather than a scary row on a fresh
/// install -- the same choice <c>ProcessorHealthCheck</c> makes when no
/// processor connection exists yet.</para>
/// </summary>
public sealed class MetadataProviderHealthCheck(IMetadataProvider metadataProvider) : IHealthCheck
{
    private const string Id = "metadata:provider";
    private const string Name = "Metadata (TMDb)";

    public string Category => HealthCheckCategories.Metadata;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var status = await metadataProvider.CheckReachabilityAsync(cancellationToken);
        if (!status.IsConfigured)
        {
            return [];
        }

        if (status.LastFailure is null)
        {
            return [HealthCheckOutcome.Ok(Id, Category, Name, status.Message)];
        }

        var failureMessage = status.LastFailure.Summary;
        return
        [
            HealthCheckOutcome.Problem(
                Id,
                Category,
                Name,
                string.IsNullOrWhiteSpace(failureMessage)
                    ? "Deluno could not reach its metadata provider."
                    : $"Deluno could not reach its metadata provider: {failureMessage}",
                cause: "TMDb, or the metadata broker, did not answer Deluno's reachability check.",
                fix: "Check the connection from this server to the internet, and confirm the key or broker URL in Settings → Metadata is still correct.",
                detailsJson: $$"""{"provider":"{{status.Provider}}","mode":"{{status.Mode}}"}""")
        ];
    }
}
