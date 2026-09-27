using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Libraries.Contracts;
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
    /// <summary>"movie" or "tv" -- Deluno keeps its movie and TV engines separate internally (see AGENTS.md), so a query always searches one or the other, never a title-agnostic "basic" mode.</summary>
    string SearchType,
    IReadOnlyList<string>? IndexerIds = null);

/// <summary>
/// Sends a hand-picked result from the manual Search page straight to a
/// chosen download client (#753). Reuses the same decision and dispatch
/// machinery a per-title grab uses -- <see cref="IAcquisitionDecisionPipeline"/>
/// and <see cref="IDownloadClientGrabService"/> -- but never
/// <c>MediaGrabHandler</c>, whose download-client and quality-profile choices
/// are resolved from a library's own routing: a manual, title-less search has
/// no library to route through, so the person chooses the client directly.
/// </summary>
public sealed record ManualSearchGrabRequest(
    string ReleaseName,
    string? IndexerId,
    string? IndexerName,
    string? DownloadUrl,
    string? Quality,
    long? SizeBytes,
    int? Seeders,
    string DownloadClientId,
    string SearchType);

public static class ManualSearchEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoManualSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/search", async (
            ManualSearchRequest request,
            IConnectionsRepository connections,
            IMediaSearchPlanner searchPlanner,
            TimeProvider timeProvider,
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

            var mediaType = string.Equals(request.SearchType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movies";
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

            var plan = await searchPlanner.BuildPlanAsync(
                query,
                year: null,
                mediaType,
                currentQuality: null,
                targetQuality: "WEB 1080p",
                sources,
                cancellationToken: cancellationToken,
                searchKind: AcquisitionSearchKinds.Interactive);

            return Results.Ok(new
            {
                candidates = MediaSearchCandidateView.ForAll(plan.Candidates),
                indexersQueried = indexers.Length
            });
        });

        endpoints.MapPost("/api/search/grab", async (
            ManualSearchGrabRequest request,
            IConnectionsRepository connections,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.ReleaseName) || string.IsNullOrWhiteSpace(request.DownloadUrl))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["downloadUrl"] = ["Choose a release with a download link."]
                });
            }

            if (string.IsNullOrWhiteSpace(request.DownloadClientId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["downloadClientId"] = ["Choose which download client to send this to."]
                });
            }

            var mediaType = string.Equals(request.SearchType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movies";

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
                request.DownloadClientId,
                new DownloadClientGrabRequest(
                    request.ReleaseName,
                    request.DownloadUrl,
                    mediaType,
                    Category: null,
                    IndexerName: request.IndexerName,
                    IndexerId: request.IndexerId),
                cancellationToken);

            return Results.Ok(new
            {
                decision.Candidate.Quality,
                decision.Candidate.DecisionStatus,
                decision.Candidate.RiskFlags,
                grabResult.Succeeded,
                grabResult.Status,
                grabResult.Message
            });
        });

        return endpoints;
    }
}
