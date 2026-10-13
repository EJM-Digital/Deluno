using Deluno.Contracts;
using Deluno.Integrations.Processors;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Processing;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Every enabled processor connection: reachable, and -- where it says --
/// watching the folder Deluno hands files to (#909).
///
/// <para>Reachability is read from <c>SystemTasks.ProcessorHealth</c>'s own
/// own loop (<see cref="Deluno.Worker.Services.ProcessorHealthWatcher"/>, 15 s while down, 30 s while healthy),
/// the same "don't duplicate a test" rule every other check here follows.
/// The watched-folder comparison is new: it asks the processor's
/// capabilities endpoint directly, because nothing else asks it.</para>
/// </summary>
public sealed class ProcessorHealthCheck(
    IProcessorRepository processors,
    IProcessorConnectionService processorConnections,
    ILibrariesRepository libraries,
    TimeProvider? timeProvider = null) : IHealthCheck
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
            // Just added and never tested: whatever added it is testing it now (#1055).
            if (connection.LastHealthTestUtc is null
                && !string.Equals(connection.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)
                && (timeProvider ?? TimeProvider.System).GetUtcNow() - connection.CreatedUtc < DownloadClientHealthCheck.NewClientGrace)
            {
                continue;
            }

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
                    cause: connection.SecretUnreadable
                        ? "The saved secret was stored on another computer, so this computer cannot read it."
                        : "Deluno could not reach this processor on its last check.",
                    fix: connection.SecretUnreadable
                        ? $"Open {connection.Name} in Connections, enter its secret again and save."
                        : $"Check that {connection.Name} is running and reachable, then test the connection.",
                    detailsJson: $$"""{"processorId":"{{connection.Id}}"}"""));
                continue;
            }

            outcomes.Add(HealthCheckOutcome.Ok(reachableId, Category, name, $"{connection.Name} is reachable."));

            // Watched-folder agreement, one row per library this processor
            // serves -- a processor can watch the right folder for one library
            // and the wrong one for another when both route through it.
            var servedLibraries = libraryList.Where(library =>
                string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase) &&
                (!string.IsNullOrWhiteSpace(library.ProcessorConnectionId)
                    ? string.Equals(library.ProcessorConnectionId, connection.Id, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(library.ProcessorName, connection.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (servedLibraries.Length == 0)
            {
                continue;
            }

            var support = await processorConnections.GetStatusSupportAsync(connection, cancellationToken);

            // The processor names its watched folder in its own filesystem
            // namespace. Translate it back to Deluno's before comparing
            // against the library's processed-output folder (#962), the same
            // as a download client's reported save folder already is in
            // FolderChainService -- otherwise a correctly mapped setup reads
            // as a folder mismatch here.
            var mappings = await processors.ListProcessorPathMappingsAsync(connection.Id, cancellationToken);
            string? ToDeluno(string? path) => mappings.Count > 0 ? ProcessorPathMappingTranslator.ToDeluno(path, mappings) ?? path : path;

            // A processor with a watched folder per library says so in its library-folders
            // list (#1144): each library takes the entry made from it, found by its id.
            var folders = support.HasLibraryFolders
                ? await processorConnections.GetLibraryFoldersAsync(connection, cancellationToken)
                : [];

            foreach (var library in servedLibraries)
            {
                var entry = ProcessorLibraryFolder.ForLibrary(folders, library.Id);
                outcomes.Add(BuildWatchedFolderOutcome(connection, library, support with { WatchedPath = ToDeluno(entry?.WatchedFolder ?? support.WatchedPath) }));
                if (ToDeluno(entry?.OutputFolder) is { Length: > 0 } outputFolder)
                {
                    outcomes.Add(BuildOutputFolderOutcome(connection, library, outputFolder));
                }
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
        var name = $"{connection.Name} watches the {library.Name} library's downloads folder";

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

        if (string.IsNullOrWhiteSpace(library.DownloadsPath))
        {
            return HealthCheckOutcome.Unknown(
                id,
                HealthCheckCategories.Processor,
                name,
                $"{connection.Name} reports watching {support.WatchedPath}, but the {library.Name} library has no downloads folder configured yet to compare it against.");
        }

        return PathsAgree(support.WatchedPath, library.DownloadsPath)
            ? HealthCheckOutcome.Ok(id, HealthCheckCategories.Processor, name, $"{connection.Name} is watching {support.WatchedPath}, the {library.Name} library's downloads folder.")
            : HealthCheckOutcome.Problem(
                id,
                HealthCheckCategories.Processor,
                name,
                $"{connection.Name} says it is watching {support.WatchedPath}, but the {library.Name} library's downloads folder is {library.DownloadsPath}.",
                cause: $"The folder {connection.Name} watches and the {library.Name} library's downloads folder no longer agree.",
                fix: $"Point {connection.Name} at {library.DownloadsPath}, or update the {library.Name} library's downloads folder to match {connection.Name}.",
                detailsJson: $$"""{"processorId":"{{connection.Id}}","libraryId":"{{library.Id}}","watchedPath":"{{support.WatchedPath}}","expectedPath":"{{library.DownloadsPath}}"}""");
    }

    /// <summary>The folder the processor writes to (its output) against the library's ready folder (#1144).</summary>
    private static HealthCheckOutcome BuildOutputFolderOutcome(
        ProcessorConnectionItem connection,
        Deluno.Libraries.Contracts.LibraryItem library,
        string outputFolder)
    {
        var id = $"processor:{connection.Id}:writing:{library.Id}";
        var name = $"{connection.Name} writes the {library.Name} library's cleaned files to the ready folder";

        if (string.IsNullOrWhiteSpace(library.ProcessorOutputPath))
        {
            return HealthCheckOutcome.Unknown(
                id,
                HealthCheckCategories.Processor,
                name,
                $"{connection.Name} reports writing to {outputFolder}, but the {library.Name} library has no ready folder configured yet to compare it against.");
        }

        return PathsAgree(outputFolder, library.ProcessorOutputPath)
            ? HealthCheckOutcome.Ok(id, HealthCheckCategories.Processor, name, $"{connection.Name} writes to {outputFolder}, the ready folder {library.Name} imports from.")
            : HealthCheckOutcome.Problem(
                id,
                HealthCheckCategories.Processor,
                name,
                $"{connection.Name} says it writes to {outputFolder}, but {library.Name} imports from {library.ProcessorOutputPath}.",
                cause: $"The folder {connection.Name} writes to and the {library.Name} library's ready folder no longer agree.",
                fix: $"Point {connection.Name}'s output at {library.ProcessorOutputPath}, or update the {library.Name} library's ready folder to match.",
                detailsJson: $$"""{"processorId":"{{connection.Id}}","libraryId":"{{library.Id}}","outputPath":"{{outputFolder}}","expectedPath":"{{library.ProcessorOutputPath}}"}""");
    }

    private static bool PathsAgree(string a, string b)
    {
        static string Normalize(string path) => path.Trim().TrimEnd('/', '\\').Replace('\\', '/');
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
}
