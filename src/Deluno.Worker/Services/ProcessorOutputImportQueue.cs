using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Series.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// The one way a processed file is queued for import (#526).
///
/// <para>The completion callback used to build its own import out of the output
/// file name: title = the release name, year = none, no grab and no library.
/// So a refined film landed as <c>Dementia.13.1963.1080p.WEB-DL… (Unknown Year)</c>
/// in whichever movie library came first, and the film that had been grabbed
/// stayed Missing. The output-folder watcher had been fixed for exactly this
/// (#268); its sibling had not. Both now come through here.</para>
/// </summary>
public sealed class ProcessorOutputImportQueue(
    IJobQueueRepository jobQueueRepository,
    IJobScheduler jobScheduler,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository) : IProcessorOutputImportQueue
{
    // The options WorkPlanner serialises every import payload with, so the
    // import handler reads this one the same way.
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ProcessorOutputImport> QueueAsync(
        LibraryItem library,
        ProcessorHandoffItem handoff,
        string outputPath,
        string source,
        CancellationToken cancellationToken)
    {
        outputPath = ImportPathFor(outputPath, library.MediaType);
        var dispatch = await jobQueueRepository.FindDispatchLinkForDownloadAsync(handoff.ClientId, handoff.QueueItemId, handoff.ReleaseName, cancellationToken);
        // Deluno is the main stop against duplicate processing (#1155): a
        // processor that sends an output for a download whose file Deluno has
        // already imported is sending a second copy.
        if (await ImportedDownloads.WasImportedAsync(jobQueueRepository, handoff.ClientId, handoff.ReleaseName, cancellationToken))
        {
            return new ProcessorOutputImport(null, dispatch?.DispatchId, dispatch?.EntityId, AlreadyImported: true);
        }

        if (await CatalogueImportNaming.RemovedSinceGrabAsync(dispatch, null, movieCatalogRepository, seriesCatalogRepository, cancellationToken) is { } removed)
        {
            // The title was removed and added again since the grab (#1155): the download
            // is the re-added title's, not an orphan, so the output goes to it.
            var readded = await CatalogueImportNaming.FindReAddedTitleAsync(dispatch, handoff.ReleaseName, movieCatalogRepository, seriesCatalogRepository, cancellationToken);
            if (readded is null)
            {
                return new ProcessorOutputImport(null, dispatch?.DispatchId, dispatch?.EntityId, removed);
            }

            dispatch = readded;
        }

        var catalogue = await CatalogueImportNaming.ResolveAsync(dispatch, movieCatalogRepository, seriesCatalogRepository, cancellationToken);

        var importRequest = new ImportExecuteRequest(
            Preview: new ImportPreviewRequest(
                SourcePath: outputPath,
                FileName: Path.GetFileName(outputPath),
                MediaType: library.MediaType,
                // Only the title of the grab. The output file's own name is not a
                // title: with no grab found it made a new film called "The Kid
                // 1921 CC 1080p BluRay x264-nikt0" (#617). No title sends the file
                // to import recovery, which says so.
                Title: catalogue.Title,
                Year: catalogue.Year ?? CatalogueImportNaming.InferYear(handoff.ReleaseName),
                Genres: catalogue.Genres,
                Tags: ["processed"],
                Studio: catalogue.Studio,
                OriginalLanguage: null,
                ImdbId: catalogue.ImdbId,
                TvDbId: catalogue.TvDbId,
                Network: catalogue.Network,
                SeriesId: catalogue.SeriesId,
                SeriesType: catalogue.SeriesType,
                NumberingScheme: catalogue.NumberingScheme,
                // The hand-off belongs to the library that grabbed the release.
                LibraryId: library.Id,
                OriginalTitle: catalogue.OriginalTitle),
            TransferMode: "auto",
            Overwrite: dispatch?.ReplacementAuthorized == true,
            AllowCopyFallback: true,
            ForceReplacement: dispatch?.ForceReplacementAuthorized == true,
            DispatchId: dispatch?.DispatchId,
            ExpectedExistingPath: dispatch?.ReplacementExpectedPath,
            ReplacementTargets: dispatch?.ReplacementTargets,
            // What the processor was given, so the import can compare what it
            // handed back against it (#574).
            ProcessedFromPath: handoff.SourcePath,
            HandoffId: handoff.Id);

        var job = await jobScheduler.EnqueueAsync(
            new EnqueueJobRequest(
                JobType: "filesystem.import.execute",
                Source: source,
                PayloadJson: JsonSerializer.Serialize(importRequest, PayloadJsonOptions),
                RelatedEntityType: library.MediaType == "tv" ? "series" : "movie",
                RelatedEntityId: catalogue.SeriesId ?? dispatch?.EntityId,
                IdempotencyKey: $"processor-output:{library.Id}:{Path.GetFullPath(outputPath).ToLowerInvariant()}"),
            cancellationToken);

        return new ProcessorOutputImport(job.Id, dispatch?.DispatchId, dispatch?.EntityId);
    }

    public Task<bool> WasAlreadyImportedAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken)
        => ImportedDownloads.WasImportedAsync(jobQueueRepository, handoff.ClientId, handoff.ReleaseName, cancellationToken);

    /// <summary>
    /// What to import when a processor hands back a folder (#619): a film is its
    /// one real video, the largest that is not a sample; an episode's folder with
    /// one video is that video; a folder of several episodes is imported whole,
    /// as the season pack it is. A file is imported as it is.
    /// </summary>
    public static string ImportPathFor(string outputPath, string mediaType)
    {
        if (!Directory.Exists(outputPath))
        {
            return outputPath;
        }

        var videos = ImportPipelineService.ListImportableVideoFiles(outputPath);
        if (videos.Count == 0)
        {
            return outputPath;
        }

        return string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? videos.Count == 1 ? videos[0] : outputPath
            : videos.OrderByDescending(video => new FileInfo(video).Length).First();
    }
}

/// <summary>
/// The catalogue's name for a grabbed release: title, year and ids, instead of
/// whatever the release name parses to (#268).
/// </summary>
internal static class CatalogueImportNaming
{
    /// <summary>
    /// The catalogue metadata behind a dispatched release, or empty values when
    /// the grab cannot be tied to a catalogue item - in which case the caller
    /// falls back to parsing the release name.
    /// </summary>
    public static async Task<CatalogueNaming> ResolveAsync(
        DispatchCatalogueLink? dispatch,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        CancellationToken cancellationToken)
    {
        if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.EntityId))
        {
            return CatalogueNaming.Empty;
        }

        if (IsShowGrab(dispatch))
        {
            var seriesId = await SeriesIdOfAsync(dispatch, seriesCatalogRepository, cancellationToken);
            var series = seriesId is null ? null : await seriesCatalogRepository.GetByIdAsync(seriesId, cancellationToken);
            return series is null
                ? CatalogueNaming.Empty
                : new CatalogueNaming(
                    series.Title,
                    series.StartYear,
                    series.ImdbId,
                    ReadMetadataText(series.MetadataJson, "TvDbId", "tvdbId", "tvdb_id"),
                    null,
                    ReadMetadataText(series.MetadataJson, "Network", "network"),
                    SplitCsv(series.Genres),
                    series.Id,
                    series.SeriesType,
                    series.NumberingScheme,
                    series.OriginalTitle);
        }

        var movie = await movieCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken);
        return movie is null
            ? CatalogueNaming.Empty
            : new CatalogueNaming(
                movie.Title,
                movie.ReleaseYear,
                movie.ImdbId,
                null,
                ReadMetadataText(movie.MetadataJson, "Studio", "studio"),
                null,
                SplitCsv(movie.Genres),
                OriginalTitle: movie.OriginalTitle);
    }

    /// <summary>
    /// The title now in the catalogue that a grab's removed title came back as (#1155): the same name and year,
    /// found after it was removed and added again. Null when there is none, or the grab was for episodes.
    /// </summary>
    public static async Task<DispatchCatalogueLink?> FindReAddedTitleAsync(
        DispatchCatalogueLink? dispatch,
        string releaseName,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        CancellationToken cancellationToken)
    {
        if (dispatch is null)
        {
            return null;
        }

        var (title, year) = ReleaseTitleMatch.ParseTitleAndYear(releaseName);
        var wanted = ReleaseTitleMatch.Clean(title);
        if (wanted.Length == 0)
        {
            return null;
        }

        if (string.Equals(dispatch.EntityType, "movie", StringComparison.OrdinalIgnoreCase))
        {
            var film = ((await movieCatalogRepository.ListAsync(cancellationToken)) ?? [])
                .FirstOrDefault(item => ReleaseTitleMatch.Clean(item.Title) == wanted && (year is null || item.ReleaseYear == year));
            return film is null ? null : dispatch with { EntityId = film.Id };
        }

        if (string.Equals(dispatch.EntityType, "series", StringComparison.OrdinalIgnoreCase))
        {
            var show = ((await seriesCatalogRepository.ListAsync(cancellationToken)) ?? [])
                .FirstOrDefault(item => ReleaseTitleMatch.Clean(item.Title) == wanted);
            return show is null ? null : dispatch with { EntityId = show.Id };
        }

        return null;
    }

    /// <summary>
    /// Why a grab must not be imported automatically any more, or null when it
    /// can be: its title, or the library that grabbed it, was removed from Deluno
    /// after the grab.
    ///
    /// <para>Without this a finished download outlived its title. The planner
    /// fell back to the first library of its type and the import, with no
    /// catalogue entry to name it from, created a new title named after the
    /// release - "Dementia 13 1963 1080p WEB DL DDP5 1 H 264 DELUNO" - a minute
    /// after the film had been removed on purpose (seen on the rig).</para>
    /// </summary>
    /// <param name="libraries">Every library Deluno has, not only the usable ones: a paused library was not removed. Null skips the library check, for a caller that already holds the library.</param>
    public static async Task<string?> RemovedSinceGrabAsync(
        DispatchCatalogueLink? dispatch,
        IReadOnlyList<LibraryItem>? libraries,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        CancellationToken cancellationToken)
    {
        if (dispatch is null)
        {
            return null;
        }

        if (libraries is not null &&
            !string.IsNullOrWhiteSpace(dispatch.LibraryId) &&
            !libraries.Any(library => string.Equals(library.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase)))
        {
            return "the library that grabbed it was removed from Deluno";
        }

        if (string.IsNullOrWhiteSpace(dispatch.EntityId))
        {
            return null;
        }

        if (string.Equals(dispatch.EntityType, "movie", StringComparison.OrdinalIgnoreCase))
        {
            return await movieCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken) is null
                ? "the film was removed from Deluno"
                : null;
        }

        if (IsShowGrab(dispatch))
        {
            var seriesId = await SeriesIdOfAsync(dispatch, seriesCatalogRepository, cancellationToken);
            if (seriesId is null)
            {
                return "the episode was removed from Deluno";
            }

            return await seriesCatalogRepository.GetByIdAsync(seriesId, cancellationToken) is null
                ? "the show was removed from Deluno"
                : null;
        }

        return null;
    }

    /// <summary>
    /// A grab for a show: the series pass records the show, an episode search
    /// records the episode (#595).
    /// </summary>
    public static bool IsShowGrab(DispatchCatalogueLink dispatch)
        => string.Equals(dispatch.EntityType, "series", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dispatch.EntityType, "episode", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The show a grab belongs to, or null when there is none any more.
    ///
    /// <para>An episode search's grab names the episode, and only a show's id
    /// was ever read here. The episode's grab fell through to the film lookup,
    /// found nothing, and was imported as a new show named after the release:
    /// six Breaking Bad episodes on the soak rig in fourteen minutes, while the
    /// real show still read them as missing and fetched one again.</para>
    /// </summary>
    public static async Task<string?> SeriesIdOfAsync(
        DispatchCatalogueLink dispatch,
        ISeriesCatalogRepository seriesCatalogRepository,
        CancellationToken cancellationToken)
        => string.Equals(dispatch.EntityType, "episode", StringComparison.OrdinalIgnoreCase)
            ? await seriesCatalogRepository.GetEpisodeSeriesIdAsync(dispatch.EntityId, cancellationToken)
            : IsShowGrab(dispatch) ? dispatch.EntityId : null;

    public static int? InferYear(string value) => ReleaseNameParser.InferYear(value);

    private static IReadOnlyList<string> SplitCsv(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? ReadMetadataText(string? metadataJson, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Provider metadata is optional and may be an older/manual blob.
        }

        return null;
    }

    public sealed record CatalogueNaming(
        string? Title,
        int? Year,
        string? ImdbId,
        string? TvDbId,
        string? Studio,
        string? Network,
        IReadOnlyList<string> Genres,
        string? SeriesId = null,
        string? SeriesType = null,
        string? NumberingScheme = null,
        // #760: the title's own original-language title, for the
        // {Original Title} naming token.
        string? OriginalTitle = null)
    {
        public static CatalogueNaming Empty { get; } = new(null, null, null, null, null, null, []);
    }
}
