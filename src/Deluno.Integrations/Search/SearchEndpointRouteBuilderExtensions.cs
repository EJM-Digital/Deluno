using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
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
            CustomFormatDryRunRequest request,
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
            var results = CustomFormatMatcher.DryRun(request.ReleaseName, formats);
            return Results.Ok(results);
        });

        return endpoints;
    }
}
