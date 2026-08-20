using Deluno.Platform.Contracts;
using Deluno.Platform.Migration;
using Deluno.Series.Contracts;
using Deluno.Series.Data;

namespace Deluno.Series.Migration;

public sealed class SeriesMigrationCatalogImporter(ISeriesCatalogRepository repository) : IMigrationCatalogImporter
{
    public string MediaType => "tv";

    public async Task<MigrationCatalogImportResult> ImportAsync(
        MigrationCatalogImportRequest request,
        CancellationToken cancellationToken)
    {
        var titles = request.Titles.Where(title => title.MediaType == MediaType).ToArray();
        if (titles.Length == 0)
        {
            return new MigrationCatalogImportResult([], []);
        }

        var libraries = request.Libraries.Where(library => library.MediaType == MediaType).ToArray();
        var applied = new List<MigrationAppliedItem>();
        var warnings = new List<string>();
        foreach (var title in titles)
        {
            var library = ResolveLibrary(title, libraries);
            if (library is null)
            {
                warnings.Add($"Did not import TV show '{title.Title}' because Deluno could not safely choose one TV library. Review its root-folder mapping first.");
                continue;
            }

            // Do a narrow indexed lookup per incoming title. Loading every
            // existing show is O(library size) even when this migration only
            // contains a handful of records.
            var existingId = await repository.FindExistingIdAsync(
                title.Title,
                title.Year,
                title.ImdbId,
                title.MetadataProvider,
                title.MetadataProviderId,
                cancellationToken);
            var series = await repository.AddAsync(new CreateSeriesRequest(
                title.Title,
                title.Year,
                title.ImdbId,
                title.Monitored,
                title.MetadataProvider,
                title.MetadataProviderId), cancellationToken);
            var alreadyPresent = existingId is not null;
            await repository.EnsureWantedStateAsync(
                series.Id,
                library.Id,
                title.SourceReportsFile ? "waiting" : "missing",
                title.SourceReportsFile
                    ? "Source reports existing episodes. Deluno will keep this show waiting until a library scan reconciles file associations."
                    : "Migrated monitored show needs accepted episodes.",
                title.SourceReportsFile,
                null,
                null,
                title.SourceReportsFile,
                cancellationToken);
            applied.Add(new MigrationAppliedItem(
                $"series-{series.Id}",
                "series",
                series.Title,
                series.Id,
                alreadyPresent ? "skipped" : "created"));
        }

        return new MigrationCatalogImportResult(applied, warnings);
    }

    private static MigrationCatalogLibrary? ResolveLibrary(MigrationCatalogTitle title, IReadOnlyList<MigrationCatalogLibrary> libraries)
    {
        if (!string.IsNullOrWhiteSpace(title.RootPath))
        {
            return libraries.FirstOrDefault(library => string.Equals(library.RootPath, title.RootPath, StringComparison.OrdinalIgnoreCase));
        }

        return libraries.Count == 1 ? libraries[0] : null;
    }
}
