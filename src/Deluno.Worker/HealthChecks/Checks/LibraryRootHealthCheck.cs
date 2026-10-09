using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Libraries.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Every library root: reachable, writable, and above the configured
/// free-space thresholds (#909). This reads <see cref="ILibraryAvailabilityService"/>
/// rather than probing paths itself -- it is the one gate search planning and
/// import automation already use, cached for a minute, so asking it again
/// here costs nothing extra and can never disagree with what actually paused
/// a library.
///
/// <para>Library pause/resume is announced by
/// <see cref="LibraryAvailabilityService"/> itself, the moment it changes,
/// not by <see cref="HealthCheckRunner"/> -- see the exclusion there. This
/// check exists so System &#8594; Health, the Dashboard tile and the bell
/// show the same row that service is already acting on, not to announce it a
/// second time.</para>
/// </summary>
public sealed class LibraryRootHealthCheck(
    ILibrariesRepository libraries,
    ILibraryAvailabilityService availability) : IHealthCheck
{
    public string Category => HealthCheckCategories.Library;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var libraryList = await libraries.ListLibrariesAsync(cancellationToken);
        var result = await availability.ReadAsync(libraryList, cancellationToken);

        var outcomes = new List<HealthCheckOutcome>();
        foreach (var library in libraryList)
        {
            if (string.IsNullOrWhiteSpace(library.RootPath))
            {
                continue;
            }

            var id = $"library:{library.Id}:root";
            var name = $"{library.Name} (library root)";

            if (!result.Statuses.TryGetValue(library.Id, out var status))
            {
                continue;
            }

            outcomes.Add(status.Problem switch
            {
                LibraryRootProblem.None when status.WarnLowFreeSpace =>
                    HealthCheckOutcome.Problem(id, Category, name, status.Message,
                        cause: $"Free space on the {library.Name} library's root is running low.",
                        fix: $"Free up space, or move some of the {library.Name} library elsewhere, before it reaches the pause threshold.",
                        detailsJson: DetailsJson(library.Id, status)),
                LibraryRootProblem.None =>
                    HealthCheckOutcome.Ok(id, Category, name, status.Message, DetailsJson(library.Id, status)),
                LibraryRootProblem.Unreachable =>
                    HealthCheckOutcome.Problem(id, Category, name, status.Message,
                        cause: $"The {library.Name} library's root could not be reached -- check that the drive or share is mounted.",
                        fix: $"Reconnect the drive or share. Deluno resumes the {library.Name} library on its own once the path answers again.",
                        detailsJson: DetailsJson(library.Id, status)),
                LibraryRootProblem.ReadOnly =>
                    HealthCheckOutcome.Problem(id, Category, name, status.Message,
                        cause: $"The {library.Name} library's root answers but refuses writes -- often a read-only mount, full permissions, or a share reconnected read-only.",
                        fix: $"Remount or repair permissions on the {library.Name} library's root so Deluno can write to it again.",
                        detailsJson: DetailsJson(library.Id, status)),
                LibraryRootProblem.LowFreeSpace =>
                    HealthCheckOutcome.Problem(id, Category, name, status.Message,
                        cause: $"Free space on the {library.Name} library's root fell to or below the configured pause threshold.",
                        fix: $"Free up space on the {library.Name} library's root. Deluno resumes it on its own once it is back above the threshold.",
                        detailsJson: DetailsJson(library.Id, status)),
                _ => HealthCheckOutcome.Unknown(id, Category, name, status.Message, DetailsJson(library.Id, status))
            });
        }

        return outcomes;
    }

    private static string DetailsJson(string libraryId, LibraryRootStatus status)
        => $$"""{"libraryId":"{{libraryId}}","reachable":{{(status.Reachable ? "true" : "false")}},"writable":{{(status.Writable is null ? "null" : status.Writable.Value ? "true" : "false")}},"freePercent":{{(status.FreePercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}}}""";
}
