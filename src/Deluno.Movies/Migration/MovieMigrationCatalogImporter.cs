using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Migration;

namespace Deluno.Movies.Migration;

public sealed class MovieMigrationCatalogImporter(IMovieCatalogRepository repository) : IMigrationCatalogImporter
{
    public string MediaType => "movies";

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
        var existingIds = (await repository.ListAsync(cancellationToken)).Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var title in titles)
        {
            var library = ResolveLibrary(title, libraries);
            if (library is null)
            {
                warnings.Add($"Did not import movie '{title.Title}' because Deluno could not safely choose one Movies library. Review its root-folder mapping first.");
                continue;
            }

            var movie = await repository.AddAsync(new CreateMovieRequest(
                title.Title,
                title.Year,
                title.ImdbId,
                title.Monitored,
                title.MetadataProvider,
                title.MetadataProviderId), cancellationToken);
            var alreadyPresent = !existingIds.Add(movie.Id);
            await repository.EnsureWantedStateAsync(
                movie.Id,
                library.Id,
                title.SourceReportsFile ? "waiting" : "missing",
                title.SourceReportsFile
                    ? "Source reports an existing file. Deluno will keep this item waiting until a library scan reconciles its file association."
                    : "Migrated monitored item needs an accepted file.",
                title.SourceReportsFile,
                null,
                null,
                title.SourceReportsFile,
                cancellationToken);
            applied.Add(new MigrationAppliedItem(
                $"movie-{movie.Id}",
                "movie",
                movie.Title,
                movie.Id,
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
