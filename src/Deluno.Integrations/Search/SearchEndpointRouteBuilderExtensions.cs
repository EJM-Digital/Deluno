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

        releases.MapPost("explain", (ReleaseExplainRequest request) =>
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
                    .ToArray()));

            return Results.Ok(new
            {
                releaseName,
                decision.Status,
                decision.Score,
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
                matchedCustomFormats = matchedFormats
            });
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
