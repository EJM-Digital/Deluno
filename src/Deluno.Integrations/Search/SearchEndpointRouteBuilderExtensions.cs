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
            var grabsByName = snapshot.GrabStats
                .GroupBy(item => item.IndexerName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => new IndexerGrabStatsItem(
                        group.Key,
                        group.Sum(item => item.TotalGrabs),
                        group.Sum(item => item.SuccessfulGrabs)),
                    StringComparer.OrdinalIgnoreCase);

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
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateReleaseProfile(
                request.Name,
                request.TagName,
                request.PreferredProtocol,
                request.UsenetDelayMinutes,
                request.TorrentDelayMinutes,
                request.PreferredTerms);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var existingTag = (await repository.ListAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(
                    item.TagName.Trim(),
                    request.TagName?.Trim() ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase));
            if (existingTag is not null)
            {
                return Results.Conflict(new { message = $"The tag '{existingTag.TagName}' already has a release profile." });
            }

            return Results.Created(
                "/api/release-profiles",
                await repository.CreateAsync(request, cancellationToken));
        });

        releaseProfiles.MapPut("{id}", async (
            string id,
            [FromBody] UpdateReleaseProfileRequest request,
            IReleaseProfileRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateReleaseProfile(
                request.Name,
                request.TagName,
                request.PreferredProtocol,
                request.UsenetDelayMinutes,
                request.TorrentDelayMinutes,
                request.PreferredTerms,
                allowMissingValues: true);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var current = await repository.GetAsync(id, cancellationToken);
            if (current is null)
            {
                return Results.NotFound();
            }

            var nextTag = request.TagName?.Trim() ?? current.TagName;
            var duplicate = (await repository.ListAsync(cancellationToken))
                .FirstOrDefault(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.TagName.Trim(), nextTag, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
            {
                return Results.Conflict(new { message = $"The tag '{duplicate.TagName}' already has a release profile." });
            }

            var updated = await repository.UpdateAsync(id, request, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        releaseProfiles.MapDelete("{id}", async (
            string id,
            IReleaseProfileRepository repository,
            CancellationToken cancellationToken) =>
            await repository.DeleteAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound());

        var releases = endpoints.MapGroup("/api/releases");
        var rankingModel = endpoints.MapGroup("/api/ranking-model");
        var intelligentRouting = endpoints.MapGroup("/api/intelligent-routing");

        releases.MapPost("explain", async (
            ReleaseExplainRequest request,
            IQualityModelService qualityModelService,
            IReleaseRankingModelService rankingModelService,
            IPlatformSettingsRepository platformSettingsRepository,
            IReleaseProfileRepository releaseProfileRepository,
            CancellationToken cancellationToken) =>
        {
            var releaseName = request.ReleaseName?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(releaseName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["releaseName"] = ["Release name is required."]
                });
            }

            var customFormats = request.CustomFormats ?? [];
            var customFormatScore = CustomFormatMatcher.EvaluateUpgradeScore(releaseName, customFormats, out var matchedFormats);
            var releaseProfiles = await releaseProfileRepository.ListApplicableAsync(request.TagNames, cancellationToken);

            var qualityModel = await qualityModelService.GetAsync(cancellationToken);
            var platformSettings = await platformSettingsRepository.GetAsync(cancellationToken);
            var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
                ReleaseName: releaseName,
                Quality: request.AssumedQuality ?? LibraryQualityDecider.DetectQuality(releaseName) ?? "WEB 1080p",
                CurrentQuality: request.CurrentQuality,
                TargetQuality: request.TargetQuality ?? "WEB 1080p",
                SizeBytes: request.SizeBytes,
                Seeders: request.Seeders,
                DownloadUrl: request.DownloadUrl ?? "https://example.com/fake",
                SourcePriorityScore: 100,
                CustomFormatScore: customFormatScore,
                    NeverGrabPatterns: request.NeverGrabPatterns?
                    .Split(['\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                ReleaseProfiles: releaseProfiles,
                IndexerProtocol: request.IndexerProtocol,
                ReleaseAgeHours: request.ReleaseAgeHours,
                MinimumAgeMinutes: request.MinimumAgeMinutes,
                RetentionDays: request.RetentionDays,
                MaximumSizeMb: request.MaximumSizeMb,
                IndexerFlags: request.IndexerFlags,
                PreferIndexerFlags: request.PreferIndexerFlags,
                AvailableUtc: request.AvailableUtc,
                AvailabilityDelayDays: request.AvailabilityDelayDays));

            var boost = rankingModelService.Score(new ReleaseRankingFeatures(
                Seeders: request.Seeders,
                SizeBytes: request.SizeBytes,
                QualityDelta: decision.QualityDelta,
                CustomFormatScore: decision.CustomFormatScore,
                SourcePriorityScore: 100,
                EstimatedBitrateMbps: decision.EstimatedBitrateMbps,
                ReleaseAgeHours: null), hardBlocked: decision.Status == "rejected");
            var scoreComputation = ReleaseScoringModePolicy.Compute(decision.Score, boost, platformSettings.SearchScoringMode);

            return Results.Ok(new
            {
                releaseName,
                decision.Status,
                decision.MeetsCutoff,
                decision.Summary,
                decision.Reasons,
                decision.RiskFlags,
                decision.QualityDelta,
                decision.ReleaseGroup,
                decision.EstimatedBitrateMbps,
                decision.PolicyVersion,
                legacyScoring = new
                {
                    label = "Legacy scoring provenance",
                    provenanceOnly = true,
                    score = scoreComputation.FinalScore,
                    ruleScore = decision.Score,
                    scoringMode = scoreComputation.Mode,
                    scoringExplanation = scoreComputation.Explanation,
                    customFormatScore = decision.CustomFormatScore,
                    seederScore = decision.SeederScore,
                    sizeScore = decision.SizeScore,
                    rankingBoost = boost,
                    matchedCustomFormats = matchedFormats
                }
            });
        });

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
            double? indexerRate = string.IsNullOrWhiteSpace(request.IndexerName)
                ? null
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

    private static Dictionary<string, string[]> ValidateReleaseProfile(
        string? name,
        string? tagName,
        string? preferredProtocol,
        int? usenetDelayMinutes,
        int? torrentDelayMinutes,
        IReadOnlyList<ReleaseTermScore>? preferredTerms,
        bool allowMissingValues = false)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (!allowMissingValues && string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this release profile a name."];
        }

        if (tagName is { Length: > 100 })
        {
            errors["tagName"] = ["The tag name must be 100 characters or fewer."];
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

    private static ScoreboardRow BuildScoreboardRow(
        IndexerItem indexer,
        IReadOnlyDictionary<string, IndexerQueryStatsItem> statsById,
        IReadOnlyDictionary<string, IndexerGrabStatsItem> grabsByName,
        int windowDays)
    {
        statsById.TryGetValue(indexer.Id, out var stats);
        var grabs = grabsByName.TryGetValue(indexer.Name, out var matchedGrabs)
            ? matchedGrabs
            : new IndexerGrabStatsItem(indexer.Name, 0, 0);
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
                windowDays));
    }

    private static ScoreboardRow BuildHistoricalScoreboardRow(
        IndexerQueryStatsItem stats,
        IReadOnlyDictionary<string, IndexerGrabStatsItem> grabsByName,
        int windowDays)
    {
        var grabs = grabsByName.TryGetValue(stats.IndexerName, out var matchedGrabs)
            ? matchedGrabs
            : new IndexerGrabStatsItem(stats.IndexerName, 0, 0);
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
                windowDays));
    }

    private static string BuildRowRecommendation(
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
        string Recommendation)
    {
        public double FailureRate => TotalQueries == 0 ? 0 : (double)FailedQueries / TotalQueries;

        public double? QueryToGrabConversion => TotalQueries == 0
            ? null
            : (double)SuccessfulGrabs / TotalQueries;
    }
}

file sealed record ReleaseExplainRequest(
    string? ReleaseName,
    string? AssumedQuality,
    string? CurrentQuality,
    string? TargetQuality,
    long? SizeBytes,
    int? Seeders,
    string? DownloadUrl,
    string? NeverGrabPatterns,
    IReadOnlyList<CustomFormatItem>? CustomFormats,
    IReadOnlyList<string>? TagNames = null,
    string? IndexerProtocol = null,
    double? ReleaseAgeHours = null,
    int? MinimumAgeMinutes = null,
    int? RetentionDays = null,
    int? MaximumSizeMb = null,
    string? IndexerFlags = null,
    string? PreferIndexerFlags = null,
    DateTimeOffset? AvailableUtc = null,
    int? AvailabilityDelayDays = null);
