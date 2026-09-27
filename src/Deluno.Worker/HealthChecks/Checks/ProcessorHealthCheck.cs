using Deluno.Contracts;
using Deluno.Integrations.Processors;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Every enabled processor connection: reachable, and -- where it says --
/// watching the folder Deluno hands files to (#909).
///
/// <para>Reachability is read from <c>SystemTasks.ProcessorHealth</c>'s own
/// five-minute pass (<see cref="WorkPlanner.RunProcessorHealthPassAsync"/>),
/// the same "don't duplicate a test" rule every other check here follows.
/// The watched-folder comparison is new: it asks the processor's
/// capabilities endpoint directly, because nothing else asks it.</para>
/// </summary>
public sealed class ProcessorHealthCheck(
    IProcessorRepository processors,
    IProcessorConnectionService processorConnections,
    ILibrariesRepository libraries) : IHealthCheck
{
    public string Category => HealthCheckCategories.Processor;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var connections = (await processors.ListProcessorConnectionsAsync(cancellationToken))
            .Where(item => item.IsEnabled)
            .ToArray();
        if (connections.Length == 0)
        {
            return [];
        }

        var libraryList = await libraries.ListLibrariesAsync(cancellationToken);
        var outcomes = new List<HealthCheckOutcome>();

        foreach (var connection in connections)
        {
            var reachableId = $"processor:{connection.Id}";
            var name = $"{connection.Name} (processor)";

            if (!string.Equals(connection.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
            {
                outcomes.Add(HealthCheckOutcome.Problem(
                    reachableId,
                    Category,
                    name,
                    connection.LastHealthMessage is { Length: > 0 } message
                        ? $"{connection.Name}: {message}"
                        : $"{connection.Name} is not answering.",
                    cause: "Deluno could not reach this processor on its last check.",
                    fix: $"Check that {connection.Name} is running and reachable, then test the connection.",
                    detailsJson: $$"""{"processorId":"{{connection.Id}}"}"""));
                continue;
            }

            outcomes.Add(HealthCheckOutcome.Ok(reachableId, Category, name, $"{connection.Name} is reachable."));

            // Watched-folder agreement, one row per library this processor
            // serves -- a processor can watch the right folder for one library
            // and the wrong one for another when both route through it.
            var servedLibraries = libraryList.Where(library =>
                string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(library.ProcessorName, connection.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (servedLibraries.Length == 0)
            {
                continue;
            }

            var support = await processorConnections.GetStatusSupportAsync(connection, cancellationToken);
            foreach (var library in servedLibraries)
            {
                outcomes.Add(BuildWatchedFolderOutcome(connection, library, support));
            }
        }

        return outcomes;
    }

    private static HealthCheckOutcome BuildWatchedFolderOutcome(
        ProcessorConnectionItem connection,
        Deluno.Libraries.Contracts.LibraryItem library,
        ProcessorStatusSupport support)
    {
        var id = $"processor:{connection.Id}:watching:{library.Id}";
        var name = $"{connection.Name} watches {library.Name}'s handed-off folder";

        if (support.Answer != ProcessorAnswer.Answered)
        {
            return HealthCheckOutcome.Unknown(
                id,
                HealthCheckCategories.Processor,
                name,
                $"{connection.Name} did not answer Deluno's capabilities check, so Deluno cannot tell what folder it is watching.");
        }

        if (string.IsNullOrWhiteSpace(support.WatchedPath))
        {
            return HealthCheckOutcome.Unknown(
                id,
                HealthCheckCategories.Processor,
                name,
                $"Not reported by the processor: {connection.Name} does not say which folder it is watching.");
        }

        if (string.IsNullOrWhiteSpace(library.ProcessorOutputPath))
        {
            return HealthCheckOutcome.Unknown(
                id,
                HealthCheckCategories.Processor,
                name,
                $"{connection.Name} reports watching {support.WatchedPath}, but the {library.Name} library has no processed-output folder configured yet to compare it against.");
        }

        return PathsAgree(support.WatchedPath, library.ProcessorOutputPath)
            ? HealthCheckOutcome.Ok(id, HealthCheckCategories.Processor, name, $"{connection.Name} is watching {support.WatchedPath}, the folder {library.Name} hands files to.")
            : HealthCheckOutcome.Problem(
                id,
                HealthCheckCategories.Processor,
                name,
                $"{connection.Name} says it is watching {support.WatchedPath}, but {library.Name} hands processed files to {library.ProcessorOutputPath}.",
                cause: "The processor's watched folder and the library's processed-output folder no longer agree.",
                fix: $"Point {connection.Name} at {library.ProcessorOutputPath}, or update the {library.Name} library's processed-output folder to match {connection.Name}.",
                detailsJson: $$"""{"processorId":"{{connection.Id}}","libraryId":"{{library.Id}}","watchedPath":"{{support.WatchedPath}}","expectedPath":"{{library.ProcessorOutputPath}}"}""");
    }

    private static bool PathsAgree(string a, string b)
    {
        static string Normalize(string path) => path.Trim().TrimEnd('/', '\\').Replace('\\', '/');
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
}
