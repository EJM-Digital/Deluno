using Deluno.Contracts;
using Deluno.Libraries;
using Deluno.Libraries.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Folders that sit on top of each other (#1325): a library's downloads or processor output folder that is, or holds, a library's
/// folder, or a library folder inside another's. Clean-up cannot tell such a folder from one that is safe to empty, so every clean-up
/// refuses to act while any of these lasts; this shows it on System &#8594; Health, naming both folders, until the library is edited.
/// Libraries saved before the rule existed are the ones that can have it; a new save with an overlap is refused outright.
/// </summary>
public sealed class LibraryFolderOverlapHealthCheck(ILibrariesRepository libraries) : IHealthCheck
{
    public string Category => HealthCheckCategories.Library;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var all = await libraries.ListLibrariesAsync(cancellationToken);
        var overlaps = LibraryFolderLayout.FindOverlaps(all.Select(LibraryFolders.From));
        var outcomes = new List<HealthCheckOutcome>();

        foreach (var library in all)
        {
            var id = $"library:{library.Id}:folder-overlap";
            var name = $"The {library.Name} library's folders";
            var mine = overlaps.Where(overlap => overlap.LibraryId == library.Id || overlap.OtherLibraryId == library.Id).ToArray();

            outcomes.Add(mine.Length == 0
                ? HealthCheckOutcome.Ok(id, Category, name, "None of this library's folders sit on top of another library's folder.")
                : HealthCheckOutcome.Problem(
                    id, Category, name,
                    mine[0].Message + " Deluno will not clean anything up until that is fixed.",
                    cause: "A folder that holds a library looks like any other folder to clean-up, so cleaning it would remove your media.",
                    fix: $"Open the {library.Name} library in Settings, Libraries and choose downloads, processor output and library folders that do not sit on top of each other. This clears itself once they do not."));
        }

        return outcomes;
    }
}
