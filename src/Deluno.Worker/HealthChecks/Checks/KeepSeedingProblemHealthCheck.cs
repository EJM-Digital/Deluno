using Deluno.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// A struck torrent Deluno meant to keep seeding and could not (#1240): the download client refused the move (the
/// category is missing, the client is down), or no keep-seeding category is set for it. The torrent is left exactly as
/// it was and nothing is deleted. The row stays here, with the reason, until a retry works or the torrent recovers or
/// leaves the client (the retry is every check), and a person can snooze it from the screen or clear it on the Keep
/// seeding screen.
/// </summary>
public sealed class KeepSeedingProblemHealthCheck(IKeepSeedingStore keepSeeding) : IHealthCheck
{
    public string Category => HealthCheckCategories.DownloadClient;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var problems = await keepSeeding.ListProblemsAsync(cancellationToken);
        return problems.Select(problem =>
        {
            var needsCategory = string.Equals(problem.Kind, KeepSeedingProblemKinds.NeedsCategory, StringComparison.OrdinalIgnoreCase);
            return HealthCheckOutcome.Problem(
                $"keep-seeding:{problem.Key}",
                Category,
                needsCategory
                    ? $"{problem.ReleaseName} has no place to keep seeding"
                    : $"{problem.ReleaseName} could not be moved to keep seeding",
                problem.Message,
                cause: needsCategory
                    ? "A rule moves private-tracker downloads to a keep-seeding category, and none is set for this one."
                    : $"{problem.ClientName} did not take the move, so the download was left alone.",
                fix: needsCategory
                    ? "Set a keep-seeding category on the Keep seeding screen: on the tag, the site or the download client."
                    : "Deluno tries again at every check. If the category is missing, create it in the download client.",
                detailsJson: System.Text.Json.JsonSerializer.Serialize(new { clientId = problem.ClientId, queueItemId = problem.ItemId, problem.Kind, problem.Category }));
        }).ToArray();
    }
}
