using Deluno.Connections.Data;
using Deluno.Contracts;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Every configured indexer, reachable (#909) -- read the same way as
/// <see cref="DownloadClientHealthCheck"/>: indexers already test themselves
/// on save and re-test through their own circuit breaker, so this only
/// surfaces that verdict on the shared health board.
/// </summary>
public sealed class IndexerHealthCheck(IConnectionsRepository connections, TimeProvider timeProvider) : IHealthCheck
{
    /// <summary>
    /// How long an indexer that has never been tested is left alone (#1349), the same grace a download client gets (#1055).
    /// One added moments ago is still being tested by whatever added it, and reading it in that gap showed "Not tested yet"
    /// as a problem for an indexer that was healthy a moment later.
    /// </summary>
    public static readonly TimeSpan NewIndexerGrace = DownloadClientHealthCheck.NewClientGrace;

    public string Category => HealthCheckCategories.Indexer;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var indexers = await connections.ListIndexersAsync(cancellationToken);
        var outcomes = new List<HealthCheckOutcome>(indexers.Count);

        var now = timeProvider.GetUtcNow();

        foreach (var indexer in indexers.Where(item => item.IsEnabled))
        {
            if (indexer.LastHealthTestUtc is null
                && !string.Equals(indexer.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)
                && now - indexer.CreatedUtc < NewIndexerGrace)
            {
                continue;
            }

            var id = $"indexer:{indexer.Id}";
            var name = $"{indexer.Name} (indexer)";

            outcomes.Add(string.Equals(indexer.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)
                ? HealthCheckOutcome.Ok(id, Category, name, $"{indexer.Name} is reachable.")
                : HealthCheckOutcome.Problem(
                    id,
                    Category,
                    name,
                    indexer.LastHealthMessage is { Length: > 0 } message
                        ? $"{indexer.Name}: {message}"
                        : $"{indexer.Name} is not answering.",
                    cause: indexer.SecretUnreadable
                        ? $"{indexer.Name}'s saved key was stored on another computer, so this computer cannot read it."
                        : indexer.RateLimitedUntilUtc is { } until && until > DateTimeOffset.UtcNow
                            ? $"{indexer.Name} rate-limited Deluno until {until:u}."
                            : $"Deluno could not confirm {indexer.Name}'s last connection test.",
                    fix: indexer.SecretUnreadable
                        ? $"Open {indexer.Name} in Connections, enter its key again and save."
                        : $"Open Connections and test {indexer.Name}.",
                    detailsJson: $$"""{"indexerId":"{{indexer.Id}}","protocol":"{{indexer.Protocol}}"}"""));
        }

        return outcomes;
    }
}
