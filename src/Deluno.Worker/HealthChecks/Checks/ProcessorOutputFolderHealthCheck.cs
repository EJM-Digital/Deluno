using Deluno.Contracts;
using Deluno.Libraries.Data;
using Deluno.Worker.Services;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// A library's processor output folder that is missing or cannot be read (#1514): finished files wait there, so until it
/// is back nothing from the processor imports. Shown here until the folder answers again; Activity carries the one line
/// (<see cref="ProcessorOutputFolderWatch"/>).
/// </summary>
public sealed class ProcessorOutputFolderHealthCheck(ILibrariesRepository libraries) : IHealthCheck
{
    public string Category => HealthCheckCategories.Processor;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<HealthCheckOutcome>();
        foreach (var library in await libraries.ListLibrariesAsync(cancellationToken))
        {
            var state = ProcessorOutputFolderWatch.Probe(library);
            if (state == ProcessorOutputFolderState.NotWatched)
            {
                continue;
            }

            var id = $"library:{library.Id}:processor-output-folder";
            var name = $"{library.Name} (processor output folder)";
            var details = $$"""{"libraryId":"{{library.Id}}"}""";
            outcomes.Add(state == ProcessorOutputFolderState.Fine
                ? HealthCheckOutcome.Ok(id, Category, name, $"The processor output folder for {library.Name} can be read.", details)
                : HealthCheckOutcome.Problem(id, Category, name,
                    ProcessorOutputFolderWatch.Describe(library, state),
                    cause: state == ProcessorOutputFolderState.Unreadable
                        ? "The folder is there but Deluno is not allowed to list it."
                        : "The folder the processor writes finished files to is not there.",
                    fix: "Reconnect the drive or share, fix the folder's permissions, or change the library's ready folder. This clears on its own once the folder can be read.",
                    detailsJson: details));
        }

        return outcomes;
    }
}
