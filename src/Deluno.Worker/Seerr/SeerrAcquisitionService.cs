using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Seerr;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Seerr;

/// <summary>
/// Implements <see cref="ISeerrAcquisitionService"/> (#851): adds the movie
/// or series a Seerr approval named, honouring the connection's own
/// library/quality-profile mapping (falling back to "the first library of
/// that media type", same as an import list with no library chosen), then
/// requests a search exactly the way adding it manually would. Lives in
/// Deluno.Worker because that is the layer that can already reach both the
/// movie and series engines — the inbound webhook route itself lives in
/// Deluno.Integrations, which cannot.
/// </summary>
public sealed class SeerrAcquisitionService(
    ILibrariesRepository librariesRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IMetadataProvider metadataProvider,
    IMediaDecisionService mediaDecisionService,
    IJobQueueRepository jobQueueRepository,
    IActivityFeedRepository activityFeedRepository,
    ISeerrAcquisitionsRepository acquisitions,
    ILogger<SeerrAcquisitionService> logger)
    : ISeerrAcquisitionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SeerrAcquisitionOutcome> AcquireAsync(
        SeerrConnection connection,
        string mediaType,
        string? tmdbId,
        string? tvdbId,
        int seerrMediaId,
        bool is4k,
        string? requestedBy,
        CancellationToken cancellationToken)
    {
        try
        {
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var resolved = ResolveLibrary(connection, mediaType, is4k, libraries);
            if (resolved.Library is null)
            {
                if (resolved.NoFourKMappingSet)
                {
                    return await FailAsync(connection, mediaType, seerrMediaId, requestedBy, "No 4K library mapped",
                        $"{connection.Name} sent a 4K request, but no 4K {(mediaType == "tv" ? "TV" : "movie")} library is mapped for it, and falling back to the normal library is turned off. Add a 4K library mapping on {connection.Name}'s card, or turn on the fallback.",
                        cancellationToken);
                }

                return await FailAsync(connection, mediaType, seerrMediaId, requestedBy, "No matching library", $"Deluno has no {(mediaType == "tv" ? "TV" : "movie")} library to add this to.", cancellationToken);
            }

            var library = resolved.Library;
            var qualityProfileId = resolved.QualityProfileId;

            var idSource = !string.IsNullOrWhiteSpace(tmdbId) ? "tmdb" : !string.IsNullOrWhiteSpace(tvdbId) ? "tvdb" : null;
            var providerId = idSource == "tmdb" ? tmdbId : tvdbId;
            if (idSource is null || string.IsNullOrWhiteSpace(providerId))
            {
                return await FailAsync(connection, mediaType, seerrMediaId, requestedBy, "No provider id", $"{connection.Name} sent no TMDb or TVDb id for this request.", cancellationToken);
            }

            var lookup = await metadataProvider.ResolveProviderRecordAsync(
                new MetadataLookupRequest(Query: null, MediaType: mediaType, Year: null, ProviderId: providerId, IdSource: idSource),
                cancellationToken);
            if (lookup.Status != MetadataProviderRecordStatus.Found || lookup.Result is null)
            {
                return await FailAsync(connection, mediaType, seerrMediaId, requestedBy, "Metadata unavailable", $"Deluno's metadata service could not identify this title from {connection.Name}'s {idSource} id.", cancellationToken);
            }

            var metadata = lookup.Result;

            string entityId;
            bool isNewlyAdded;
            if (mediaType == "tv")
            {
                var existingId = await seriesCatalogRepository.FindExistingIdAsync(metadata.Title, metadata.Year, metadata.ImdbId, metadata.Provider, metadata.ProviderId, cancellationToken);
                if (existingId is null)
                {
                    var created = await seriesCatalogRepository.AddAsync(BuildSeriesRequest(metadata), cancellationToken);
                    entityId = created.Id;
                    isNewlyAdded = true;
                }
                else
                {
                    entityId = existingId;
                    isNewlyAdded = false;
                }

                var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                    MediaType: library.MediaType, HasFile: false, CurrentQuality: null, CutoffQuality: library.CutoffQuality,
                    UpgradeUntilCutoff: library.UpgradeUntilCutoff, UpgradeUnknownItems: library.UpgradeUnknownItems, IsReleased: true));
                await seriesCatalogRepository.EnsureWantedStateAsync(entityId, library.Id, decision.WantedStatus, decision.WantedReason, false, decision.CurrentQuality, decision.TargetQuality, decision.QualityCutoffMet, cancellationToken);
                if (!string.IsNullOrWhiteSpace(qualityProfileId))
                {
                    await seriesCatalogRepository.UpdateQualityProfileAsync(entityId, qualityProfileId!, cancellationToken);
                }
            }
            else
            {
                var existingId = await movieCatalogRepository.FindExistingIdAsync(metadata.Title, metadata.Year, metadata.ImdbId, metadata.Provider, metadata.ProviderId, cancellationToken);
                if (existingId is null)
                {
                    var created = await movieCatalogRepository.AddAsync(BuildMovieRequest(metadata), cancellationToken);
                    entityId = created.Id;
                    isNewlyAdded = true;
                }
                else
                {
                    entityId = existingId;
                    isNewlyAdded = false;
                }

                var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                    MediaType: library.MediaType, HasFile: false, CurrentQuality: null, CutoffQuality: library.CutoffQuality,
                    UpgradeUntilCutoff: library.UpgradeUntilCutoff, UpgradeUnknownItems: library.UpgradeUnknownItems, IsReleased: true));
                await movieCatalogRepository.EnsureWantedStateAsync(entityId, library.Id, decision.WantedStatus, decision.WantedReason, false, decision.CurrentQuality, decision.TargetQuality, decision.QualityCutoffMet, cancellationToken);
                if (!string.IsNullOrWhiteSpace(qualityProfileId))
                {
                    await movieCatalogRepository.UpdateQualityProfileAsync(entityId, qualityProfileId!, cancellationToken);
                }
            }

            var searchRequested = await jobQueueRepository.RequestLibrarySearchAsync(
                new LibraryAutomationPlanItem(
                    LibraryId: library.Id,
                    LibraryName: library.Name,
                    MediaType: library.MediaType,
                    AutoSearchEnabled: library.AutoSearchEnabled,
                    MissingSearchEnabled: library.MissingSearchEnabled,
                    UpgradeSearchEnabled: library.UpgradeSearchEnabled,
                    SearchIntervalMinutes: library.SearchIntervalMinutes,
                    RetryDelayHours: library.RetryDelayHours,
                    MaxItemsPerRun: library.MaxItemsPerRun,
                    MaxMissingItemsPerRun: library.MaxMissingItemsPerRun,
                    MaxUpgradeItemsPerRun: library.MaxUpgradeItemsPerRun,
                    SearchWindowDays: library.SearchWindowDays,
                    SearchWindowStartHour: library.SearchWindowStartHour,
                    SearchWindowEndHour: library.SearchWindowEndHour),
                cancellationToken);

            await acquisitions.RecordAsync(connection.Id, connection.Name, mediaType, entityId, metadata.Title, seerrMediaId, "added", null, requestedBy, cancellationToken);

            var titleWithQuality = is4k ? $"\"{metadata.Title}\" (4K)" : $"\"{metadata.Title}\"";
            var who = string.IsNullOrWhiteSpace(requestedBy) ? string.Empty : $" — requested by {requestedBy}";
            var message = isNewlyAdded
                ? $"{connection.Name} approved {titleWithQuality} — added to {library.Name} and {(searchRequested ? "queued a search" : "search already covered by schedule")}.{who}"
                : $"{connection.Name} approved {titleWithQuality} — already in {library.Name}; {(searchRequested ? "queued a search" : "search already covered by schedule")}.{who}";
            await activityFeedRepository.RecordActivityAsync("seerr.acquired", message, JsonSerializer.Serialize(new { connection.Id, connection.Name, mediaType, entityId, seerrMediaId, is4k, requestedBy }, JsonOptions), null, "seerr-server", connection.Id, cancellationToken);

            return new SeerrAcquisitionOutcome(true, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Seerr connection {ConnectionId} failed turning an approval into an acquisition.", connection.Id);
            return await FailAsync(connection, mediaType, seerrMediaId, requestedBy, "Unexpected error", ex.Message, cancellationToken);
        }
    }

    private async Task<SeerrAcquisitionOutcome> FailAsync(SeerrConnection connection, string mediaType, int seerrMediaId, string? requestedBy, string title, string message, CancellationToken cancellationToken)
    {
        await acquisitions.RecordAsync(connection.Id, connection.Name, mediaType, entityId: string.Empty, title, seerrMediaId, "failed", message, requestedBy, cancellationToken);
        await activityFeedRepository.RecordActivityAsync("seerr.acquire-failed", $"{connection.Name}: {message}", null, null, "seerr-server", connection.Id, cancellationToken);
        return new SeerrAcquisitionOutcome(false, message);
    }

    /// <summary>What library and quality profile a request (4K or not) resolves to (#985).</summary>
    private sealed record ResolvedLibrary(LibraryItem? Library, string? QualityProfileId, bool NoFourKMappingSet);

    private static ResolvedLibrary ResolveLibrary(SeerrConnection connection, string mediaType, bool is4k, IReadOnlyList<LibraryItem> libraries)
    {
        var candidates = libraries.Where(item => string.Equals(item.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0)
        {
            return new ResolvedLibrary(null, null, false);
        }

        if (is4k)
        {
            var fourKLibraryId = mediaType == "tv" ? connection.Series4kLibraryId : connection.Movie4kLibraryId;
            var fourKQualityProfileId = mediaType == "tv" ? connection.Series4kQualityProfileId : connection.Movie4kQualityProfileId;
            if (!string.IsNullOrWhiteSpace(fourKLibraryId))
            {
                var exact = candidates.FirstOrDefault(item => string.Equals(item.Id, fourKLibraryId, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                {
                    return new ResolvedLibrary(exact, fourKQualityProfileId, false);
                }
            }

            if (!connection.Fallback4kToNormalLibrary)
            {
                return new ResolvedLibrary(null, null, true);
            }

            // Falls back to the normal library/profile below.
        }

        var preferredId = mediaType == "tv" ? connection.SeriesLibraryId : connection.MovieLibraryId;
        var preferredQualityProfileId = mediaType == "tv" ? connection.SeriesQualityProfileId : connection.MovieQualityProfileId;
        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            var exact = candidates.FirstOrDefault(item => string.Equals(item.Id, preferredId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return new ResolvedLibrary(exact, preferredQualityProfileId, false);
            }
        }

        return new ResolvedLibrary(candidates[0], preferredQualityProfileId, false);
    }

    private static CreateSeriesRequest BuildSeriesRequest(MetadataSearchResult metadata)
        => new(
            Title: metadata.Title,
            StartYear: metadata.Year,
            ImdbId: metadata.ImdbId,
            Monitored: true,
            MetadataProvider: metadata.Provider,
            MetadataProviderId: metadata.ProviderId,
            OriginalTitle: metadata.OriginalTitle,
            Overview: metadata.Overview,
            PosterUrl: metadata.PosterUrl,
            BackdropUrl: metadata.BackdropUrl,
            Rating: metadata.Rating,
            Genres: string.Join(", ", metadata.Genres),
            ExternalUrl: metadata.ExternalUrl,
            MetadataJson: JsonSerializer.Serialize(metadata, JsonOptions));

    private static CreateMovieRequest BuildMovieRequest(MetadataSearchResult metadata)
        => new(
            Title: metadata.Title,
            ReleaseYear: metadata.Year,
            ImdbId: metadata.ImdbId,
            Monitored: true,
            MetadataProvider: metadata.Provider,
            MetadataProviderId: metadata.ProviderId,
            OriginalTitle: metadata.OriginalTitle,
            Overview: metadata.Overview,
            PosterUrl: metadata.PosterUrl,
            BackdropUrl: metadata.BackdropUrl,
            Rating: metadata.Rating,
            Genres: string.Join(", ", metadata.Genres),
            ExternalUrl: metadata.ExternalUrl,
            MetadataJson: JsonSerializer.Serialize(metadata, JsonOptions));
}
