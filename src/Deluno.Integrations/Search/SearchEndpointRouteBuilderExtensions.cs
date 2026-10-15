using Microsoft.AspNetCore.Mvc;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Quality.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Jobs.Contracts;
using Deluno.Quality.Contracts;

namespace Deluno.Integrations.Search;

public static class SearchEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Registers the <c>POST /api/custom-formats/dry-run</c> endpoint.
    /// This lives in <c>Deluno.Integrations</c> so it can reference
    /// <see cref="CustomFormatMatcher"/> while reading formats from
    /// <see cref="IQualityRepository"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapDelunoSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Every tracker Deluno can search itself (#606), for the picker. Ones
        // the engine cannot run are listed with the reason, so a tracker the
        // user looks for is never simply missing.
        endpoints.MapGet("/api/trackers", (Trackers.ITrackerDefinitionCatalog catalog) => Results.Ok(catalog.List()));

        // #986: where the bundled definitions come from and under what terms,
        // so the notice travels with them and is readable from the app.
        endpoints.MapGet("/api/trackers/definitions/notice", () => Results.Text(Trackers.TrackerDefinitionCatalog.BundledNotice(), "text/markdown"));

        // #756: the scheduled/manual tracker-definition update's own state --
        // on/off, cadence, and what its last pass did.
        endpoints.MapGet("/api/trackers/definitions/update-check", async (
            Trackers.ITrackerDefinitionUpdateService updateService,
            CancellationToken cancellationToken) => Results.Ok(await updateService.GetAsync(cancellationToken)));

        endpoints.MapPut("/api/trackers/definitions/update-check", async (
            Trackers.UpdateTrackerDefinitionUpdateSettingsRequest request,
            Trackers.ITrackerDefinitionUpdateService updateService,
            CancellationToken cancellationToken) => Results.Ok(
                await updateService.SetSettingsAsync(request.IsEnabled, request.IntervalHours, cancellationToken)));

        endpoints.MapPost("/api/trackers/definitions/update-check/run", async (
            Trackers.ITrackerDefinitionUpdateService updateService,
            CancellationToken cancellationToken) => Results.Ok(await updateService.UpdateNowAsync(cancellationToken)));

        // Whether a Cloudflare helper answers at an address (#628), before it is saved.
        endpoints.MapPost("/api/trackers/cloudflare-helper/test", async (
            CloudflareHelperTestRequest request,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Url) || !Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var address)
                || address.Scheme is not ("http" or "https"))
            {
                return Results.Ok(new { healthy = false, message = "Enter the helper's web address starting with http:// or https://, such as http://127.0.0.1:8191." });
            }

            var http = httpClientFactory.CreateClient(Trackers.FlareSolverrCloudflareHelper.HttpClientName);
            var (healthy, message) = await Trackers.CloudflareHelperProbe.CheckAsync(http, address, cancellationToken);
            return Results.Ok(new { healthy, message });
        });

        var indexerScoreboard = endpoints.MapGroup("/api/indexers");

        indexerScoreboard.MapGet("scoreboard", async (
            int? days,
            IConnectionsRepository connectionsRepository,
            IIndexerQueryStatsRepository statsRepository,
            CancellationToken cancellationToken) =>
        {
            var windowDays = Math.Clamp(days ?? 30, 1, 365);
            var toUtc = DateTimeOffset.UtcNow;
            var fromUtc = toUtc.AddDays(-windowDays);
            var snapshot = await statsRepository.GetScoreboardAsync(fromUtc, toUtc, cancellationToken);
            var configuredIndexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
            var statsById = snapshot.QueryStats.ToDictionary(item => item.IndexerId, StringComparer.OrdinalIgnoreCase);
            // Grabs are filed under the indexer's id (#1077), so a renamed indexer
            // keeps them. Only a dispatch whose source matched no indexer is left
            // to be found by the name it carries.
            var grabsByName = new GrabStatsLookup(snapshot.GrabStats);

            var rows = configuredIndexers
                .Select(indexer => BuildScoreboardRow(indexer, statsById, grabsByName, windowDays))
                .Concat(snapshot.QueryStats
                    .Where(item => !configuredIndexers.Any(indexer => string.Equals(indexer.Id, item.IndexerId, StringComparison.OrdinalIgnoreCase)))
                    .Select(item => BuildHistoricalScoreboardRow(item, grabsByName, windowDays)))
                .OrderByDescending(item => item.SuccessfulGrabs)
                .ThenByDescending(item => item.TotalQueries)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Ok(new
            {
                windowDays,
                fromUtc,
                toUtc,
                activeIndexers = configuredIndexers.Count(item => item.IsEnabled),
                totalIndexers = configuredIndexers.Count,
                totalQueries = snapshot.TotalQueries,
                totalGrabs = snapshot.TotalGrabs,
                successfulGrabs = snapshot.SuccessfulGrabs,
                conversionRate = snapshot.TotalQueries == 0
                    ? (double?)null
                    : (double)snapshot.SuccessfulGrabs / snapshot.TotalQueries,
                insight = BuildScoreboardInsight(rows, windowDays),
                indexers = rows
            });
        });

        var customFormats = endpoints.MapGroup("/api/custom-formats");
        var releaseProfiles = endpoints.MapGroup("/api/release-profiles");

        customFormats.MapPost("dry-run", async (
            [FromBody] CustomFormatDryRunRequest request,
            IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.ReleaseName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["releaseName"] = ["Release name is required."]
                });
            }

            var formats = await repository.ListCustomFormatsAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.MediaType))
            {
                formats = formats
                    .Where(format => string.Equals(format.MediaType, request.MediaType, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            var results = CustomFormatMatcher.DryRun(request.ReleaseName, formats);
            return Results.Ok(results);
        });

        releaseProfiles.MapGet(string.Empty, async (
            IReleaseProfileRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListAsync(cancellationToken)));

        releaseProfiles.MapGet("{id}", async (
            string id,
            IReleaseProfileRepository repository,
            CancellationToken cancellationToken) =>
        {
            var item = await repository.GetAsync(id, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        releaseProfiles.MapPost(string.Empty, async (
            [FromBody] CreateReleaseProfileRequest request,
            IReleaseProfileRepository repository,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateReleaseProfile(
                request.Name,
                request.TagName,
                request.PreferredProtocol,
                request.UsenetDelayMinutes,
                request.TorrentDelayMinutes,
                request.PreferredTerms,
                mustContain: request.MustContain,
                mustNotContain: request.MustNotContain,
                tagNames: request.TagNames);
            foreach (var scopedIndexerId in request.IndexerIds ?? [])
            {
                await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(connectionFactory, errors, "indexerIds", Deluno.Infrastructure.Storage.ReferenceKind.Indexer, scopedIndexerId, null, cancellationToken);
                if (errors.ContainsKey("indexerIds"))
                {
                    break;
                }
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var requestedTags = request.TagNames ?? (request.TagName is null ? [] : [request.TagName]);
            var taken = ReleaseRuleTagConflict(await repository.ListAsync(cancellationToken), null, requestedTags);
            if (taken is not null)
            {
                return Results.Conflict(new { message = taken });
            }

            return Results.Created(
                "/api/release-profiles",
                await repository.CreateAsync(request, cancellationToken));
        });

        releaseProfiles.MapPut("{id}", async (
            string id,
            [FromBody] UpdateReleaseProfileRequest request,
            IReleaseProfileRepository repository,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateReleaseProfile(
                request.Name,
                request.TagName,
                request.PreferredProtocol,
                request.UsenetDelayMinutes,
                request.TorrentDelayMinutes,
                request.PreferredTerms,
                allowMissingValues: true,
                mustContain: request.MustContain,
                mustNotContain: request.MustNotContain,
                tagNames: request.TagNames);
            foreach (var scopedIndexerId in request.IndexerIds ?? [])
            {
                await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(connectionFactory, errors, "indexerIds", Deluno.Infrastructure.Storage.ReferenceKind.Indexer, scopedIndexerId, null, cancellationToken);
                if (errors.ContainsKey("indexerIds"))
                {
                    break;
                }
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var current = await repository.GetAsync(id, cancellationToken);
            if (current is null)
            {
                return Results.NotFound();
            }

            var nextTags = request.TagNames
                ?? (request.TagName is null ? current.AppliesToTags : [request.TagName]);
            var taken = ReleaseRuleTagConflict(await repository.ListAsync(cancellationToken), id, nextTags);
            if (taken is not null)
            {
                return Results.Conflict(new { message = taken });
            }

            var updated = await repository.UpdateAsync(id, request, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        // #995: how several matching rules are used, and the order of the rules.
        releaseProfiles.MapGet("mode", async (
            IReleaseProfileRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(new ReleaseRuleSettings(await repository.GetRuleModeAsync(cancellationToken))));

        releaseProfiles.MapPut("mode", async (
            [FromBody] ReleaseRuleSettings request,
            IReleaseProfileRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(new ReleaseRuleSettings(await repository.SetRuleModeAsync(request.Mode, cancellationToken))));

        releaseProfiles.MapPut("order", async (
            [FromBody] ReorderReleaseProfilesRequest request,
            IReleaseProfileRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.ReorderAsync(request.Ids ?? [], cancellationToken)));

        releaseProfiles.MapDelete("{id}", async (
            string id,
            IReleaseProfileRepository repository,
            CancellationToken cancellationToken) =>
            await repository.DeleteAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound());

        var rankingModel = endpoints.MapGroup("/api/ranking-model");
        var intelligentRouting = endpoints.MapGroup("/api/intelligent-routing");

        rankingModel.MapGet("status", (IReleaseRankingModelService rankingModelService) =>
        {
            return Results.Ok(rankingModelService.GetStatus());
        });

        rankingModel.MapPost("train", async (
            IReleaseRankingModelAdminService rankingModelAdminService,
            CancellationToken cancellationToken) =>
        {
            var result = await rankingModelAdminService.TrainAsync("manual", cancellationToken);
            return result.Success
                ? Results.Ok(result)
                : Results.BadRequest(result);
        });

        rankingModel.MapPost("rollback", (
            RankingModelRollbackRequest request,
            IReleaseRankingModelAdminService rankingModelAdminService) =>
        {
            var rolledBack = rankingModelAdminService.TryRollback(request.Version, out var message);
            return rolledBack
                ? Results.Ok(new { accepted = true, message, version = request.Version })
                : Results.BadRequest(new { accepted = false, message, version = request.Version });
        });

        intelligentRouting.MapGet("snapshot", async (
            IIntelligentRoutingService intelligentRoutingService,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await intelligentRoutingService.GetSnapshotAsync(cancellationToken);
            return Results.Ok(snapshot);
        });

        intelligentRouting.MapGet("anomalies", async (
            IIntelligentRoutingService intelligentRoutingService,
            CancellationToken cancellationToken) =>
        {
            var anomalies = await intelligentRoutingService.DetectAnomaliesAsync(cancellationToken);
            return Results.Ok(new
            {
                count = anomalies.Count,
                anomalies
            });
        });

        intelligentRouting.MapPost("recommend-release", async (
            IntelligentReleaseRecommendationRequest request,
            IReleaseRankingModelService rankingModelService,
            IIntelligentRoutingService intelligentRoutingService,
            IPlatformSettingsRepository platformSettingsRepository,
            IConnectionsRepository connectionsRepository,
            CancellationToken cancellationToken) =>
        {
            var boost = rankingModelService.Score(new ReleaseRankingFeatures(
                Seeders: request.Seeders,
                SizeBytes: request.SizeBytes,
                QualityDelta: request.QualityDelta,
                CustomFormatScore: request.CustomFormatScore,
                SourcePriorityScore: request.SourcePriorityScore,
                EstimatedBitrateMbps: request.EstimatedBitrateMbps,
                ReleaseAgeHours: request.ReleaseAgeHours), hardBlocked: false);
            var platformSettings = await platformSettingsRepository.GetAsync(cancellationToken);
            var scoreComputation = ReleaseScoringModePolicy.Compute(0, boost, platformSettings.SearchScoringMode);

            var snapshot = await intelligentRoutingService.GetSnapshotAsync(cancellationToken);
            // Success rates are filed under the indexer's id (#1077); the name only finds a source that matched no indexer.
            var rateIndexer = string.IsNullOrWhiteSpace(request.IndexerName)
                ? null
                : (await connectionsRepository.ListIndexersAsync(cancellationToken))
                    .FirstOrDefault(item => string.Equals(item.Name, request.IndexerName.Trim(), StringComparison.OrdinalIgnoreCase));
            double? indexerRate = string.IsNullOrWhiteSpace(request.IndexerName)
                ? null
                : rateIndexer is not null && snapshot.IndexerSuccessRates.TryGetValue(rateIndexer.Id, out var idRate) ? idRate
                : snapshot.IndexerSuccessRates.TryGetValue(request.IndexerName, out var rate) ? rate : null;
            var clientRate = await intelligentRoutingService.GetDownloadClientSuccessRateAsync(request.DownloadClientId, cancellationToken);

            var recommendation = 50d;
            recommendation += Math.Clamp(scoreComputation.FinalScore / 40d, -20, 20);
            if (indexerRate is not null)
            {
                recommendation += (indexerRate.Value - 0.5d) * 24d;
            }

            if (clientRate is not null)
            {
                recommendation += (clientRate.Value - 0.5d) * 24d;
            }

            if (request.CustomFormatScore >= snapshot.Preferences.AverageCustomFormatScore)
            {
                recommendation += 4;
            }

            if (request.QualityDelta > 0)
            {
                recommendation += 6;
            }

            var finalScore = (int)Math.Round(Math.Clamp(recommendation, 0, 100), MidpointRounding.AwayFromZero);
            var label = finalScore >= 75
                ? "strong"
                : finalScore >= 55
                    ? "review"
                    : "avoid";

            return Results.Ok(new IntelligentReleaseRecommendation(
                RecommendationScore: finalScore,
                RecommendationLabel: label,
                Summary: $"Recommendation {finalScore}/100 ({label}) for {request.ReleaseName}.",
                IndexerSuccessRate: indexerRate,
                DownloadClientSuccessRate: clientRate,
                RankingBoost: boost,
                ScoringMode: scoreComputation.Mode));
        });

        return endpoints;
    }

    /// <summary>
    /// #994: Sonarr's DelayProfileTagInUseValidator - a tag belongs to one rule
    /// only, so which rule a tagged title uses is never a guess. Null when the tags are free.
    /// </summary>
    private static string? ReleaseRuleTagConflict(
        IReadOnlyList<ReleaseProfileItem> existing,
        string? ignoreId,
        IReadOnlyList<string> tags)
    {
        var wanted = tags
            .SelectMany(tag => (tag ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        if (wanted.Length == 0)
        {
            // The rule with no tag is the default; there can be only one.
            var otherDefault = existing.FirstOrDefault(item =>
                !string.Equals(item.Id, ignoreId, StringComparison.OrdinalIgnoreCase)
                && item.AppliesToTags.Count == 0);
            return otherDefault is null ? null : $"'{otherDefault.Name}' is already the rule for titles with no tag.";
        }

        foreach (var tag in wanted)
        {
            var owner = existing.FirstOrDefault(item =>
                !string.Equals(item.Id, ignoreId, StringComparison.OrdinalIgnoreCase)
                && item.AppliesToTags.Contains(tag, StringComparer.OrdinalIgnoreCase));
            if (owner is not null)
            {
                return $"The tag '{tag}' already belongs to the rule '{owner.Name}'. A tag can be in one rule only.";
            }
        }

        return null;
    }

    private static Dictionary<string, string[]> ValidateReleaseProfile(
        string? name,
        string? tagName,
        string? preferredProtocol,
        int? usenetDelayMinutes,
        int? torrentDelayMinutes,
        IReadOnlyList<ReleaseTermScore>? preferredTerms,
        bool allowMissingValues = false,
        string? mustContain = null,
        string? mustNotContain = null,
        IReadOnlyList<string>? tagNames = null)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (!allowMissingValues && string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this release profile a name."];
        }

        if (tagName is { Length: > 100 } || tagNames?.Any(tag => tag.Length > 100) == true)
        {
            errors["tagName"] = ["The tag name must be 100 characters or fewer."];
        }

        // #996: a term written between slashes is a pattern; say so plainly when one cannot be read.
        foreach (var (field, text) in new[] { ("mustContain", mustContain), ("mustNotContain", mustNotContain) })
        {
            var problem = ReleaseTermPattern.Split(text)
                .Select(ReleaseTermPattern.Validate)
                .FirstOrDefault(message => message is not null);
            if (problem is not null)
            {
                errors[field] = [problem];
            }
        }

        if (preferredProtocol is not null
            && !new[] { "any", "usenet", "torrent" }.Contains(preferredProtocol.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            errors["preferredProtocol"] = ["Choose any, usenet, or torrent."];
        }

        if (usenetDelayMinutes is < 0 or > 43_200)
        {
            errors["usenetDelayMinutes"] = ["Usenet delay must be between 0 and 43,200 minutes."];
        }

        if (torrentDelayMinutes is < 0 or > 43_200)
        {
            errors["torrentDelayMinutes"] = ["Torrent delay must be between 0 and 43,200 minutes."];
        }

        if (preferredTerms is not null && preferredTerms.Any(item =>
                string.IsNullOrWhiteSpace(item.Term) || item.Term.Length > 100 || item.Score is < -10_000 or > 10_000))
        {
            errors["preferredTerms"] = ["Every preferred term needs text (100 characters or fewer) and a score between -10,000 and 10,000."];
        }

        return errors;
    }

    /// <summary>Grab counts by the indexer's id, with the name as the way back to a dispatch that never matched one.</summary>
    private sealed class GrabStatsLookup
    {
        private readonly Dictionary<string, IndexerGrabStatsItem> byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IndexerGrabStatsItem> byName = new(StringComparer.OrdinalIgnoreCase);

        public GrabStatsLookup(IEnumerable<IndexerGrabStatsItem> items)
        {
            foreach (var item in items)
            {
                var target = string.IsNullOrWhiteSpace(item.IndexerId) ? byName : byId;
                var key = string.IsNullOrWhiteSpace(item.IndexerId) ? item.IndexerName : item.IndexerId;
                target[key] = target.TryGetValue(key, out var seen)
                    ? seen with { TotalGrabs = seen.TotalGrabs + item.TotalGrabs, SuccessfulGrabs = seen.SuccessfulGrabs + item.SuccessfulGrabs }
                    : item;
            }
        }

        public IndexerGrabStatsItem For(string indexerId, string indexerName)
        {
            byId.TryGetValue(indexerId, out var linked);
            byName.TryGetValue(indexerName, out var byTheName);
            return new IndexerGrabStatsItem(
                indexerName,
                (linked?.TotalGrabs ?? 0) + (byTheName?.TotalGrabs ?? 0),
                (linked?.SuccessfulGrabs ?? 0) + (byTheName?.SuccessfulGrabs ?? 0),
                indexerId);
        }
    }

    private static ScoreboardRow BuildScoreboardRow(
        IndexerItem indexer,
        IReadOnlyDictionary<string, IndexerQueryStatsItem> statsById,
        GrabStatsLookup grabsByName,
        int windowDays)
    {
        statsById.TryGetValue(indexer.Id, out var stats);
        var grabs = grabsByName.For(indexer.Id, indexer.Name);
        return new ScoreboardRow(
            Id: indexer.Id,
            Name: indexer.Name,
            IsEnabled: indexer.IsEnabled,
            HealthStatus: indexer.HealthStatus,
            TotalQueries: stats?.TotalQueries ?? 0,
            SearchQueries: stats?.SearchQueries ?? 0,
            RssQueries: stats?.RssQueries ?? 0,
            AuthQueries: stats?.AuthQueries ?? 0,
            FailedQueries: stats?.FailedQueries ?? 0,
            AverageResponseMilliseconds: stats?.AverageResponseMilliseconds,
            CandidatesReturned: stats?.CandidatesReturned ?? 0,
            TotalGrabs: grabs.TotalGrabs,
            SuccessfulGrabs: grabs.SuccessfulGrabs,
            Recommendation: BuildRowRecommendation(
                indexer.Name,
                stats?.TotalQueries ?? 0,
                stats?.FailedQueries ?? 0,
                stats?.AverageResponseMilliseconds,
                grabs.SuccessfulGrabs,
                windowDays,
                stats?.HeldBackQueries ?? 0),
            LastQueryUtc: stats?.LastQueryUtc,
            HeldBackQueries: stats?.HeldBackQueries ?? 0);
    }

    private static ScoreboardRow BuildHistoricalScoreboardRow(
        IndexerQueryStatsItem stats,
        GrabStatsLookup grabsByName,
        int windowDays)
    {
        var grabs = grabsByName.For(stats.IndexerId, stats.IndexerName);
        return new ScoreboardRow(
            Id: stats.IndexerId,
            Name: stats.IndexerName,
            IsEnabled: false,
            HealthStatus: "removed",
            TotalQueries: stats.TotalQueries,
            SearchQueries: stats.SearchQueries,
            RssQueries: stats.RssQueries,
            AuthQueries: stats.AuthQueries,
            FailedQueries: stats.FailedQueries,
            AverageResponseMilliseconds: stats.AverageResponseMilliseconds,
            CandidatesReturned: stats.CandidatesReturned,
            TotalGrabs: grabs.TotalGrabs,
            SuccessfulGrabs: grabs.SuccessfulGrabs,
            Recommendation: BuildRowRecommendation(
                stats.IndexerName,
                stats.TotalQueries,
                stats.FailedQueries,
                stats.AverageResponseMilliseconds,
                grabs.SuccessfulGrabs,
                windowDays,
                stats.HeldBackQueries),
            LastQueryUtc: stats.LastQueryUtc,
            HeldBackQueries: stats.HeldBackQueries);
    }

    private static string BuildRowRecommendation(
        string name,
        long totalQueries,
        long failedQueries,
        double? averageResponseMilliseconds,
        long successfulGrabs,
        int windowDays,
        long heldBackQueries = 0)
    {
        // #1330: searches Deluno itself held back are not queries and never a failure; say so in plain words.
        var heldBack = heldBackQueries == 0
            ? string.Empty
            : $" {heldBackQueries:N0} {(heldBackQueries == 1 ? "search was" : "searches were")} held back by your limit.";
        return BuildSentRecommendation(name, totalQueries, failedQueries, averageResponseMilliseconds, successfulGrabs, windowDays) + heldBack;
    }

    private static string BuildSentRecommendation(
        string name,
        long totalQueries,
        long failedQueries,
        double? averageResponseMilliseconds,
        long successfulGrabs,
        int windowDays)
    {
        if (totalQueries == 0)
        {
            return $"No query history for {name} in the last {windowDays} days.";
        }

        var failureRate = (double)failedQueries / totalQueries;
        if (failureRate >= 0.25)
        {
            return $"Needs attention: {failureRate:P0} of {totalQueries:N0} queries failed in the last {windowDays} days.";
        }

        if (successfulGrabs == 0)
        {
            return $"Answered {totalQueries:N0} queries but produced no successful grabs in the last {windowDays} days.";
        }

        var latency = averageResponseMilliseconds is null
            ? "an unknown latency"
            : $"{averageResponseMilliseconds.Value:N0} ms average latency";
        return $"Answered {totalQueries:N0} queries and produced {successfulGrabs:N0} successful grabs at {latency}.";
    }

    private static string BuildScoreboardInsight(IReadOnlyList<ScoreboardRow> rows, int windowDays)
    {
        var queried = rows.Where(item => item.TotalQueries > 0).ToArray();
        if (queried.Length == 0)
        {
            return $"No indexer queries have been recorded in the last {windowDays} days. Deluno will build the scoreboard as searches and health checks run.";
        }

        var best = queried
            .OrderByDescending(item => item.SuccessfulGrabs)
            .ThenByDescending(item => item.TotalQueries)
            .First();
        var quiet = queried
            .Where(item => item.SuccessfulGrabs == 0)
            .OrderByDescending(item => item.TotalQueries)
            .FirstOrDefault();
        if (quiet is not null)
        {
            return $"{quiet.Name} answered {quiet.TotalQueries:N0} queries without a successful grab in this window; {best.Name} produced the most successful grabs ({best.SuccessfulGrabs:N0}).";
        }

        return $"{best.Name} produced the most successful grabs ({best.SuccessfulGrabs:N0}) from {best.TotalQueries:N0} queries in this window.";
    }

    private sealed record ScoreboardRow(
        string Id,
        string Name,
        bool IsEnabled,
        string HealthStatus,
        long TotalQueries,
        long SearchQueries,
        long RssQueries,
        long AuthQueries,
        long FailedQueries,
        double? AverageResponseMilliseconds,
        long CandidatesReturned,
        long TotalGrabs,
        long SuccessfulGrabs,
        string Recommendation,
        DateTimeOffset? LastQueryUtc = null,
        long HeldBackQueries = 0)
    {
        public double FailureRate => TotalQueries == 0 ? 0 : (double)FailedQueries / TotalQueries;

        public double? QueryToGrabConversion => TotalQueries == 0
            ? null
            : (double)SuccessfulGrabs / TotalQueries;
    }
}

/// <summary>An address to check for a Cloudflare helper (#628).</summary>
public sealed record CloudflareHelperTestRequest(string? Url);
