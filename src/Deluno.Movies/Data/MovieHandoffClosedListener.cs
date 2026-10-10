using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Movies.Data;

/// <summary>
/// A hand-off that was dismissed closes the movies import issues about it (#1228): they are the same problem, counted by
/// Activity, the bell and System, and a person who cleared the hand-off has cleared it. Their System rows go with them.
/// </summary>
public sealed class MovieHandoffClosedListener(IServiceProvider services) : IHandoffClosedListener
{
    public async Task HandoffClosedAsync(string handoffId, CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<IMovieCatalogRepository>();
        var summary = await repository.GetImportRecoverySummaryAsync(cancellationToken);
        foreach (var recoveryCase in summary.RecentCases.Where(item =>
                     string.Equals(ProblemTwins.HandoffIdIn(item.DetailsJson), handoffId, StringComparison.OrdinalIgnoreCase)))
        {
            if (await repository.ResolveImportRecoveryCaseAsync(recoveryCase.Id, "dismissed", cancellationToken) is null)
            {
                continue;
            }

            await repository.AddImportRecoveryEventAsync(recoveryCase.Id, "case_dismissed", "Closed with its hand-off.", null, cancellationToken);
            await ProblemTwins.ClearHealthRowAsync(
                recoveryCase.Id,
                services.GetService<IHealthCheckRepository>(),
                services.GetService<IRealtimeEventPublisher>(),
                cancellationToken);
        }
    }
}
