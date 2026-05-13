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
