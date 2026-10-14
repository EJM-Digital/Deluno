using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search.Trackers;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Integrations.Search;

/// <summary>
/// A free-text query against a chosen set of enabled indexers, no title
/// required (#753) -- Prowlarr's own Search screen. Deluno's normal search
/// is otherwise always for a specific movie or episode, against that title's
/// own library and quality profile; this asks the same fan-out
/// (<see cref="IMediaSearchPlanner"/>) a plain question instead.
/// </summary>
public sealed record ManualSearchRequest(
    string Query,
    /// <summary>
    /// "movie", "tv", or "basic". Movie and TV still run through their own
    /// separate engine call each (AGENTS.md: the two stay separate
    /// internally) -- "basic" simply asks both and combines what came back,
    /// the way Prowlarr's own basic search type is not itself a third engine.
    /// </summary>
    string SearchType,
    IReadOnlyList<string>? IndexerIds = null,
    /// <summary>Newznab/Torznab category numbers to restrict the query to. Null or empty asks every category each indexer covers for the chosen type(s).</summary>
    IReadOnlyList<int>? CategoryIds = null);

/// <summary>
/// Sends a hand-picked result from the manual Search page to a download
/// client (#753). Reuses the same decision (<see cref="IAcquisitionDecisionPipeline"/>)
/// and dispatch (<see cref="IDownloadClientGrabService"/>) machinery a
/// per-title grab uses. A manual, title-less search has no library of its
/// own to route a client through, so the client is resolved the same way a
/// per-title grab's own routing would for a library of this release's media
/// type (<see cref="IAcquisitionDecisionPipeline.ChooseDownloadClientAsync"/>
/// against that media type's own routed clients) -- never a client the
/// person hand-picks here, which would bypass the torrent/Usenet and tag
/// routing every other grab respects.
/// </summary>
public sealed record ManualSearchGrabRequest(
    string ReleaseName,
    string? IndexerId,
    string? IndexerName,
    string? DownloadUrl,
    string? Quality,
    long? SizeBytes,
    int? Seeders,
    string SearchType);

public static class ManualSearchEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoManualSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/search/categories", () =>
            Results.Ok(TrackerCategories.Standard
                .OrderBy(pair => pair.Value)
                .Select(pair => new { value = pair.Value, label = pair.Key })
                .ToArray()));

        endpoints.MapPost("/api/search", async (
            ManualSearchRequest request,
            IConnectionsRepository connections,
            IMediaSearchPlanner searchPlanner,
            TimeProvider timeProvider,
            IIndexerHistoryRecorder indexerHistoryRecorder,
            CancellationToken cancellationToken) =>
        {
            var query = request.Query?.Trim() ?? string.Empty;
            if (query.Length == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["query"] = ["Type something to search for."]
                });
            }

            // "basic" is not a third engine (AGENTS.md keeps movie and TV
            // separate internally) -- it asks both of the existing engines
            // and combines what they found, the same way a person choosing
            // "basic" in Prowlarr is not choosing a different search kind
            // from movie/TV, just not committing to one.
            var mediaTypes = request.SearchType?.Trim().ToLowerInvariant() switch
            {
                "tv" => new[] { "tv" },
                "basic" => new[] { "movies", "tv" },
                _ => new[] { "movies" }
            };

            var indexers = (await connections.ListIndexersAsync(cancellationToken))
                .Where(indexer => indexer.IsEnabled)
                .Where(indexer => request.IndexerIds is not { Count: > 0 } || request.IndexerIds.Contains(indexer.Id))
                .ToArray();

            if (indexers.Length == 0)
            {
                return Results.Ok(new { candidates = Array.Empty<object>(), indexersQueried = 0 });
            }

            var now = timeProvider.GetUtcNow();
            var sources = indexers
                .Select(indexer => new LibrarySourceLinkItem(
                    Id: $"manual-search:{indexer.Id}",
                    LibraryId: "manual-search",
                    IndexerId: indexer.Id,
                    IndexerName: indexer.Name,
                    Priority: 0,
                    RequiredTags: string.Empty,
                    ExcludedTags: string.Empty,
                    CreatedUtc: now,
                    UpdatedUtc: now))
                .ToArray();

            var candidates = new List<object>();
            var matchedIndexerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failedIndexers = new Dictionary<string, IntegrationFailure>(StringComparer.OrdinalIgnoreCase);
            foreach (var mediaType in mediaTypes)
            {
                var family = mediaType == "tv" ? 5 : 2;
                var categoriesForType = request.CategoryIds is { Count: > 0 }
                    ? request.CategoryIds.Where(category => category / 1000 == family).ToArray()
                    : null;

                var plan = await searchPlanner.BuildPlanAsync(
                    query,
                    year: null,
                    mediaType,
                    currentQuality: null,
                    targetQuality: "WEB 1080p",
                    sources,
                    cancellationToken: cancellationToken,
                    searchKind: AcquisitionSearchKinds.Interactive,
                    categoryIds: categoriesForType,
                    queryIsATitle: false);

                foreach (var failure in plan.Failures ?? [])
                {
                    failedIndexers.TryAdd(failure.ServiceId, failure);
                }

                foreach (var candidate in plan.Candidates)
                {
                    // Flattened rather than reusing MediaSearchCandidateView.For
                    // directly: a "basic" result needs its own SearchType
                    // alongside every field, so a grab from it (which is told
                    // SearchType rather than guessing it from the release name)
                    // routes through the right engine.
                    candidates.Add(new
                    {
                        candidate.ReleaseName,
                        candidate.IndexerId,
                        candidate.IndexerName,
                        candidate.Quality,
                        candidate.MeetsCutoff,
                        candidate.DecisionStatus,
                        candidate.DecisionReasons,
                        candidate.RiskFlags,
                        candidate.RejectionGate,
                        candidate.SizeBytes,
                        candidate.Seeders,
                        candidate.Leechers,
                        candidate.Grabs,
                        candidate.IndexerProtocol,
                        candidate.ReleaseAgeHours,
                        candidate.DownloadUrl,
                        SearchType = mediaType
                    });
                    if (!string.IsNullOrWhiteSpace(candidate.IndexerId))
                    {
                        matchedIndexerIds.Add(candidate.IndexerId);
                    }
                }
            }

            // #978: every indexer this Search-page query reached shows up in
            // Indexer History against its own name, the same way an
            // interactive per-title search already does -- even one that came
            // back with nothing, since a search was still made against it.
            foreach (var indexer in indexers)
            {
                var found = matchedIndexerIds.Contains(indexer.Id);
                if (!found && failedIndexers.TryGetValue(indexer.Id, out var failure))
                {
                    // An indexer that refused or errored did not "find nothing" (#1311). A refused key is already in
                    // the history as a sign-in failure, written where it was found; any other failure is written here.
                    if (failure.Kind != IntegrationFailureKind.Authentication)
                    {
                        await indexerHistoryRecorder.RecordSearchAsync(
                            indexer.Id,
                            indexer.Name,
                            "failed",
                            $"Search page: \"{query}\" failed: {failure.Message}",
                            detailsJson: null,
                            cancellationToken);
                    }

                    continue;
                }

                await indexerHistoryRecorder.RecordSearchAsync(
                    indexer.Id,
                    indexer.Name,
                    found ? "matched" : "no_results",
                    found ? $"Search page: \"{query}\" matched a release" : $"Search page: \"{query}\" found nothing",
                    detailsJson: null,
                    cancellationToken);
            }

            return Results.Ok(new
            {
                candidates,
                indexersQueried = indexers.Length
            });
        });

        endpoints.MapPost("/api/search/grab", async (
            ManualSearchGrabRequest request,
            ILibrariesRepository libraries,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IIndexerHistoryRecorder indexerHistoryRecorder,
            IJobQueueRepository jobQueueRepository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.ReleaseName) || string.IsNullOrWhiteSpace(request.DownloadUrl))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["downloadUrl"] = ["Choose a release with a download link."]
                });
            }

            var mediaType = string.Equals(request.SearchType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movies";
            var mediaLabel = mediaType == "tv" ? "TV" : "movie";

            var allLibraries = await libraries.ListLibrariesAsync(cancellationToken);
            var librariesOfType = allLibraries
                .Where(library => string.Equals(library.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (librariesOfType.Length == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["downloadClient"] = [$"Add a {mediaLabel} library with a linked download client before grabbing from Search."]
                });
            }

            // No title means no one library to route through, so this pools
            // every library of the release's own media type -- movies and TV
            // stay apart from each other, but not from the rest of their own
            // kind -- and picks a client from their combined routing the same
            // way ChooseDownloadClientAsync would for any one of them,
            // instead of a client the person hand-picks here.
            var routedClients = new List<LibraryDownloadClientLinkItem>();
            var seenClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in librariesOfType)
            {
                var routing = await libraries.GetLibraryRoutingAsync(library.Id, cancellationToken);
                if (routing is null)
                {
                    continue;
                }

                foreach (var client in routing.DownloadClients)
                {
                    if (seenClientIds.Add(client.DownloadClientId))
                    {
                        routedClients.Add(client);
                    }
                }
            }

            if (routedClients.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["downloadClient"] = [$"Link a download client to a {mediaLabel} library before grabbing a release."]
                });
            }

            var downloadClient = await acquisitionPipeline.ChooseDownloadClientAsync(
                request.IndexerId,
                routedClients,
                cancellationToken,
                titleTags: null);
            if (downloadClient is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["downloadClient"] = [await acquisitionPipeline.ExplainNoDownloadClientAsync(request.IndexerId, routedClients, cancellationToken)
                        ?? $"No {mediaLabel} library has a download client that can take a release from {request.IndexerName ?? "this source"}. Link a matching torrent or Usenet client in Library Routing."]
                });
            }

            var category = string.IsNullOrWhiteSpace(downloadClient.Category) ? null : downloadClient.Category.Trim();

            // Evaluated for the same reasons a per-title grab is (quality
            // read-out, risk flags) even though there is no library profile
            // to weigh it against here -- a manual, title-less pick is
            // already the person's own explicit decision, so it is never
            // held back the way an automatic search's pick would be.
            var decision = acquisitionPipeline.EvaluateSelectedRelease(new AcquisitionSelectedReleaseRequest(
                ReleaseName: request.ReleaseName,
                IndexerId: request.IndexerId,
                IndexerName: request.IndexerName,
                DownloadUrl: request.DownloadUrl,
                CurrentQuality: null,
                TargetQuality: request.Quality,
                CandidateQuality: request.Quality,
                SizeBytes: request.SizeBytes,
                Seeders: request.Seeders,
                ForceOverride: true,
                MediaType: mediaType));

            var grabResult = await downloadClientGrabService.GrabAsync(
                downloadClient.DownloadClientId,
                new DownloadClientGrabRequest(
                    request.ReleaseName,
                    request.DownloadUrl,
                    mediaType,
                    category,
                    IndexerName: request.IndexerName,
                    IndexerId: request.IndexerId),
                cancellationToken);

            // #1062: Deluno only sees and acts on downloads it sent, so a release
            // sent from the Search page gets its own dispatch record like every
            // other grab: without one the download would run in the client
            // where Transfers, Queue, Health and cleanup could never see it.
            // There is no title to file it under, so it is recorded as a
            // release, in the library whose routing the client came from, and
            // is found again by the id the grab already knows.
            var homeLibrary = librariesOfType.FirstOrDefault(library => string.Equals(library.Id, downloadClient.LibraryId, StringComparison.OrdinalIgnoreCase))
                ?? librariesOfType[0];
            // A hash a title's own dispatch already owns is followed, not recorded
            // a second time as a title-less one (#1062).
            var onItsWay = grabResult.Succeeded && grabResult.IsDuplicateFollow;
            if (!onItsWay)
            {
                await jobQueueRepository.RecordDownloadDispatchAsync(
                homeLibrary.Id,
                mediaType,
                "release",
                Guid.CreateVersion7().ToString("N"),
                request.ReleaseName,
                request.IndexerName ?? string.Empty,
                downloadClient.DownloadClientId,
                downloadClient.DownloadClientName,
                grabResult.Status,
                notesJson: JsonSerializer.Serialize(new { chosenOnSearch = true, decision }),
                grabResponseCode: grabResult.Succeeded ? 200 : 400,
                grabFailureCode: grabResult.Failure?.Code ?? grabResult.FailureCode,
                cancellationToken: cancellationToken,
                failure: grabResult.Failure,
                clientExternalId: grabResult.ExternalId,
                startedBy: DownloadStartedBy.Manual);
            }

            // #978: a grab made from the Search page shows in Indexer
            // History against its indexer, the same as a per-title grab.
            await indexerHistoryRecorder.RecordGrabAsync(
                request.IndexerId,
                request.IndexerName ?? "Unknown indexer",
                grabResult.Succeeded ? "succeeded" : "failed",
                grabResult.Succeeded
                    ? $"Search page grab: {request.ReleaseName}"
                    : $"Search page grab: {request.ReleaseName} ({grabResult.Message ?? grabResult.Status})",
                detailsJson: null,
                cancellationToken);

            return Results.Ok(new
            {
                decision.Candidate.Quality,
                decision.Candidate.DecisionStatus,
                decision.Candidate.RiskFlags,
                downloadClient.DownloadClientName,
                grabResult.Succeeded,
                grabResult.Status,
                Message = onItsWay ? $"{downloadClient.DownloadClientName} already has this one and Deluno is following it: it is already on its way." : grabResult.Message
            });
        });

        return endpoints;
    }
}
