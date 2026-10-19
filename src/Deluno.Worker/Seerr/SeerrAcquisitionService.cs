using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Seerr;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Libraries.Routing;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Seerr;

/// <summary>
/// Implements <see cref="ISeerrAcquisitionService"/> (#851): adds the movie
/// or series a Seerr approval named, then requests a search exactly the way
/// adding it manually would. Lives in Deluno.Worker because that is the
/// layer that can already reach both the movie and series engines — the
/// inbound webhook route itself lives in Deluno.Integrations, which cannot.
///
/// <para>#1033: which library it lands in is decided by
/// <see cref="ILibraryRoutingResolver"/> — the same destination rules a
/// manual add's folder placement already reads, evaluated here against the
/// title's own metadata (genre, anime, original language, "requested in
/// 4K") — with the connection's own library as the last-resort fallback,
/// same as before #1033. A #985 4K mapping became a "requested in 4K" rule
/// when the install upgraded (see
/// <c>V0121SeerrRequestsUseLibraryRoutingRules</c>), so an existing 4K
/// mapping still lands exactly where it always did.</para>
/// </summary>
public sealed class SeerrAcquisitionService(
    ILibrariesRepository librariesRepository,
    ILibraryRoutingResolver libraryRoutingResolver,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IMetadataProvider metadataProvider,
    IMediaDecisionService mediaDecisionService,
    IJobQueueRepository jobQueueRepository,
    IActivityFeedRepository activityFeedRepository,
    ISeerrAcquisitionsRepository acquisitions,
    ILogger<SeerrAcquisitionService> logger,
    ITitleTagService? titleTagService = null,
    IAutoTaggingService? autoTagging = null)
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

            // #1033: placed by the same routing rules a manual add's folder
            // placement already reads, evaluated on the title's metadata —
            // not a fixed per-connection mapping. The connection's own
            // library (still called "fallback" below) is only tried when no
            // rule matches.
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var rules = await librariesRepository.ListDestinationRulesAsync(cancellationToken);
            var fallbackLibraryId = mediaType == "tv" ? connection.SeriesLibraryId : connection.MovieLibraryId;

            // #1046: the connection's own tags (and its extra 4K ones) go on the title
            // before anything reads them, so routing, the search and notifications see
            // them. The title may not exist yet, so routing is handed the tags it is
            // about to carry: what it already has plus what this connection gives.
            var tagKind = mediaType == "tv" ? MediaKind.Series : MediaKind.Movie;
            var givenTags = TagNameList.Split(is4k ? $"{connection.Tags},{connection.Tags4k}" : connection.Tags);
            string? existingEntityId = mediaType == "tv"
                ? await seriesCatalogRepository.FindExistingIdAsync(metadata.Title, metadata.Year, metadata.ImdbId, metadata.Provider, metadata.ProviderId, cancellationToken)
                : await movieCatalogRepository.FindExistingIdAsync(metadata.Title, metadata.Year, metadata.ImdbId, metadata.Provider, metadata.ProviderId, cancellationToken);
            var routingTags = givenTags.ToList();
            if (existingEntityId is not null && titleTagService is not null)
            {
                routingTags.AddRange(await titleTagService.ListNamesAsync(tagKind, existingEntityId, cancellationToken));
            }

            // #1024: the tags the auto-tagging rules would give this title are known before it
            // exists, so a tag rule can send a request to a library just like a tag from the
            // connection can.
            if (autoTagging is not null)
            {
                routingTags.AddRange(await autoTagging.PredictAsync(
                    AutoTagFactsBuilder.ForNewTitle(
                        tagKind,
                        metadata.Title,
                        metadata.Year,
                        metadata.Genres,
                        metadata.OriginalLanguage,
                        metadata.Certification,
                        mediaType == "tv" ? metadata.Network : metadata.Studio,
                        metadata.RuntimeMinutes,
                        metadata.Keywords ?? [],
                        metadata.Status,
                        metadata.Collection,
                        routingTags,
                        new AutoTagAddFacts(is4k, true, null)),
                    cancellationToken));
            }

            var routing = libraryRoutingResolver.Resolve(
                mediaType,
                new LibraryRoutingMetadata(metadata.Genres, metadata.OriginalLanguage, metadata.Certification, metadata.Studio, is4k, routingTags),
                libraries,
                rules,
                // #1035: a 4K request never falls back to the normal library.
                is4k ? null : fallbackLibraryId);

            if (!routing.Matched && is4k)
            {
                return await FailAsync(connection, mediaType, seerrMediaId, requestedBy, "No rule placed this 4K request",
                    $"This 4K request for \"{metadata.Title}\" was refused: no routing rule placed it, and a 4K request is never put in the normal library. Add a \"requested in 4K\" rule on the Destination Rules page.",
                    cancellationToken);
            }

            if (!routing.Matched)
            {
                var hasLibraryOfType = libraries.Any(item => string.Equals(item.MediaType, mediaType, StringComparison.OrdinalIgnoreCase));
                if (!hasLibraryOfType)
                {
                    return await FailAsync(connection, mediaType, seerrMediaId, requestedBy, "No matching library", $"Deluno has no {(mediaType == "tv" ? "TV" : "movie")} library to add this to.", cancellationToken);
                }

                return await FailAsync(connection, mediaType, seerrMediaId, requestedBy, "No destination rule matched",
                    $"No destination rule matched \"{metadata.Title}\", and {connection.Name} has no fallback {(mediaType == "tv" ? "TV" : "movie")} library set. Add a routing rule on the Destination Rules page, or set a fallback library on {connection.Name}'s card.",
                    cancellationToken);
            }

            var library = routing.Library!;
            var rule = routing.Rule;
            // A matched rule routes by library, so that library's own
            // default quality profile applies; the fallback library pairs
            // with the connection's own fallback quality profile, same as
            // before #1033.
            var qualityProfileId = rule is null
                ? (mediaType == "tv" ? connection.SeriesQualityProfileId : connection.MovieQualityProfileId)
                : null;

            string entityId;
            bool isNewlyAdded;
            if (mediaType == "tv")
            {
                var existingId = existingEntityId;
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

                // A title Deluno already holds a file for stays as it is: the wanted-state upsert overwrites
                // has_file and the status, so an approval used to put an owned title back on the wanted list.
                var owned = isNewlyAdded ? null : await seriesCatalogRepository.GetByIdAsync(entityId, cancellationToken);
                if (owned?.HasFile != true)
                {
                    var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                        MediaType: library.MediaType, HasFile: false, CurrentQuality: null, CutoffQuality: library.CutoffQuality,
                        UpgradeUntilCutoff: library.UpgradeUntilCutoff, UpgradeUnknownItems: library.UpgradeUnknownItems, IsReleased: true));
                    await seriesCatalogRepository.EnsureWantedStateAsync(entityId, library.Id, decision.WantedStatus, decision.WantedReason, false, decision.CurrentQuality, decision.TargetQuality, decision.QualityCutoffMet, cancellationToken);
                }
                if (!string.IsNullOrWhiteSpace(qualityProfileId))
                {
                    await seriesCatalogRepository.UpdateQualityProfileAsync(entityId, qualityProfileId!, cancellationToken);
                }
            }
            else
            {
                var existingId = existingEntityId;
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

                // A title Deluno already holds a file for stays as it is: the wanted-state upsert overwrites
                // has_file and the status, so an approval used to put an owned title back on the wanted list.
                var owned = isNewlyAdded ? null : await movieCatalogRepository.GetByIdAsync(entityId, cancellationToken);
                if (owned?.HasFile != true)
                {
                    var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                        MediaType: library.MediaType, HasFile: false, CurrentQuality: null, CutoffQuality: library.CutoffQuality,
                        UpgradeUntilCutoff: library.UpgradeUntilCutoff, UpgradeUnknownItems: library.UpgradeUnknownItems, IsReleased: true));
                    await movieCatalogRepository.EnsureWantedStateAsync(entityId, library.Id, decision.WantedStatus, decision.WantedReason, false, decision.CurrentQuality, decision.TargetQuality, decision.QualityCutoffMet, cancellationToken);
                }
                if (!string.IsNullOrWhiteSpace(qualityProfileId))
                {
                    await movieCatalogRepository.UpdateQualityProfileAsync(entityId, qualityProfileId!, cancellationToken);
                }
            }

            // Tags are written straight after the title exists, before its search is
            // requested and before the acquisition and Activity lines are recorded.
            IReadOnlyList<string> newlyTagged = [];
            if (titleTagService is not null && givenTags.Count > 0)
            {
                newlyTagged = await titleTagService.GiveAsync(tagKind, entityId, givenTags, cancellationToken);
            }

            // #1024: the rules again, now the title exists and its library is known, so a rule
            // that looks at the library or the quality profile can act too.
            if (autoTagging is not null)
            {
                await autoTagging.ApplyAsync(
                    tagKind,
                    entityId,
                    isNewlyAdded ? AutoTagTrigger.Added : AutoTagTrigger.Edited,
                    isNewlyAdded ? new AutoTagAddFacts(is4k, true, null) : null,
                    [library.Id],
                    cancellationToken);
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

            await acquisitions.RecordAsync(connection.Id, connection.Name, mediaType, entityId, metadata.Title, seerrMediaId, "added", null, requestedBy, cancellationToken, rule?.Id, rule?.Name);

            var titleWithQuality = is4k ? $"\"{metadata.Title}\" (4K)" : $"\"{metadata.Title}\"";
            var who = string.IsNullOrWhiteSpace(requestedBy) ? string.Empty : $" — requested by {requestedBy}";
            // #1033: says which rule placed it (or that the fallback library did), so
            // the automatic choice is visible in Activity, not silent.
            var placedBy = rule is not null
                ? (routing.MatchedTag is { Length: > 0 } matchedTag ? $"placed by the tag “{matchedTag}” (the “{rule.Name}” rule)" : $"placed by the “{rule.Name}” rule") : $"using {connection.Name}'s fallback library";
            var tagged = newlyTagged.Count > 0 ? $" Tagged {string.Join(", ", newlyTagged)}." : string.Empty;
            var message = isNewlyAdded
                ? $"{connection.Name} approved {titleWithQuality} — added to {library.Name} ({placedBy}) and {(searchRequested ? "queued a search" : "search already covered by schedule")}.{tagged}{who}"
                : $"{connection.Name} approved {titleWithQuality} — already in {library.Name} ({placedBy}); {(searchRequested ? "queued a search" : "search already covered by schedule")}.{tagged}{who}";
            await activityFeedRepository.RecordActivityAsync("seerr.acquired", message, JsonSerializer.Serialize(new { connection.Id, connection.Name, mediaType, entityId, seerrMediaId, is4k, requestedBy, ruleId = rule?.Id, ruleName = rule?.Name }, JsonOptions), null, "seerr-server", connection.Id, cancellationToken);

            return new SeerrAcquisitionOutcome(true, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Seerr connection {ConnectionId} failed turning an approval into an acquisition.", connection.Id);
            return await FailAsync(connection, mediaType, seerrMediaId, requestedBy, "Unexpected error", PlainFailure.Describe(ex, "adding a request"), cancellationToken);
        }
    }

    private async Task<SeerrAcquisitionOutcome> FailAsync(SeerrConnection connection, string mediaType, int seerrMediaId, string? requestedBy, string title, string message, CancellationToken cancellationToken)
    {
        await acquisitions.RecordAsync(connection.Id, connection.Name, mediaType, entityId: string.Empty, title, seerrMediaId, "failed", message, requestedBy, cancellationToken);
        await activityFeedRepository.RecordActivityAsync("seerr.acquire-failed", $"{connection.Name}: {message}", null, null, "seerr-server", connection.Id, cancellationToken);
        return new SeerrAcquisitionOutcome(false, message);
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
