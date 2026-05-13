using Microsoft.AspNetCore.Mvc;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Quality;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Integrations.Search;

public static class SearchEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Registers the <c>POST /api/custom-formats/dry-run</c> endpoint.
    /// This lives in <c>Deluno.Integrations</c> so it can reference
    /// <see cref="CustomFormatMatcher"/> while reading formats from
    /// <see cref="IPlatformSettingsRepository"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapDelunoSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var customFormats = endpoints.MapGroup("/api/custom-formats");

        customFormats.MapPost("dry-run", async (
            [FromBody] CustomFormatDryRunRequest request,
            IPlatformSettingsRepository repository,
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

        var releases = endpoints.MapGroup("/api/releases");
        var rankingModel = endpoints.MapGroup("/api/ranking-model");
        var intelligentRouting = endpoints.MapGroup("/api/intelligent-routing");

        releases.MapPost("explain", async (
            ReleaseExplainRequest request,
            IQualityModelService qualityModelService,
            IReleaseRankingModelService rankingModelService,
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
            var customFormatScore = CustomFormatMatcher.Evaluate(releaseName, customFormats, out var matchedFormats);

            var qualityModel = await qualityModelService.GetAsync(cancellationToken);
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
                    .ToArray()), qualityModel);

            var boost = rankingModelService.Score(new ReleaseRankingFeatures(
                Seeders: request.Seeders,
                SizeBytes: request.SizeBytes,
                QualityDelta: decision.QualityDelta,
                CustomFormatScore: decision.CustomFormatScore,
                SourcePriorityScore: 100,
                EstimatedBitrateMbps: decision.EstimatedBitrateMbps,
                ReleaseAgeHours: null), hardBlocked: decision.Status == "rejected");

            return Results.Ok(new
            {
                releaseName,
                decision.Status,
                Score = decision.Score + boost.BoostPoints,
                decision.MeetsCutoff,
                decision.Summary,
                decision.Reasons,
                decision.RiskFlags,
                decision.QualityDelta,
                decision.CustomFormatScore,
                decision.SeederScore,
                decision.SizeScore,
                decision.ReleaseGroup,
                decision.EstimatedBitrateMbps,
                decision.PolicyVersion,
                rankingBoost = boost,
                matchedCustomFormats = matchedFormats
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

            var snapshot = await intelligentRoutingService.GetSnapshotAsync(cancellationToken);
            double? indexerRate = string.IsNullOrWhiteSpace(request.IndexerName)
                ? null
                : snapshot.IndexerSuccessRates.TryGetValue(request.IndexerName, out var rate) ? rate : null;
            var clientRate = await intelligentRoutingService.GetDownloadClientSuccessRateAsync(request.DownloadClientId, cancellationToken);

            var recommendation = 50d;
            recommendation += Math.Clamp(boost.BoostPoints, -20, 20);
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
                RankingBoost: boost));
        });

        return endpoints;
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
    IReadOnlyList<CustomFormatItem>? CustomFormats);
