using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Platform;

public static class PlatformEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup("/api/settings");

        settings.MapGet("/", async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var snapshot = await repository.GetAsync(cancellationToken);
            return Results.Ok(snapshot);
        });

        settings.MapPut("/", async (
            UpdatePlatformSettingsRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var snapshot = await repository.SaveAsync(request, cancellationToken);
            return Results.Ok(snapshot);
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> Validate(UpdatePlatformSettingsRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.AppInstanceName))
        {
            errors["appInstanceName"] = ["An app name is required."];
        }

        return errors;
    }
}
