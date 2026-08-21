using Deluno.Contracts;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.Presets;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Quality;

/// <summary>
/// /api/quality-profiles, /api/quality-model, /api/quality-profile-presets,
/// /api/custom-formats and /api/policy-sets. Split out of
/// PlatformEndpointRouteBuilderExtensions by ADR-001 Step 1; handler bodies
/// are unchanged apart from the repository type and explicit [FromServices].
/// Policy-set create/update still apply to assigned libraries via
/// <see cref="IPolicySetLibraryApplier"/> because Libraries has not moved
/// out of Platform yet.
/// </summary>
public static class QualityEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoQualityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var qualityProfiles = endpoints.MapGroup("/api/quality-profiles");
        var qualityModel = endpoints.MapGroup("/api/quality-model");
        var customFormats = endpoints.MapGroup("/api/custom-formats");
        var policySets = endpoints.MapGroup("/api/policy-sets");

        qualityProfiles.MapGet(string.Empty, async ([FromServices] IQualityRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListQualityProfilesAsync(cancellationToken);
            return Results.Ok(items);
        });

        qualityProfiles.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateQualityProfileRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateQualityProfile(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateQualityProfileAsync(request, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("QualityProfile", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        qualityProfiles.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateQualityProfileRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateQualityProfile(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateQualityProfileAsync(id, request, cancellationToken);
            if (item is not null)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("QualityProfile", item.Id, cancellationToken);
            }
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        qualityProfiles.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] IQualityRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteQualityProfileAsync(id, cancellationToken);
            if (removed)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("QualityProfile", id, cancellationToken);
            }
            return removed ? Results.NoContent() : Results.NotFound();
        });

        qualityProfiles.MapPut("order", async (
            HttpContext httpContext,
            [FromBody] ReorderQualityProfilesRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.Ids is null || request.Ids.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ids"] = ["Provide at least one quality profile id."]
                });
            }

            await repository.ReorderQualityProfilesAsync(request.Ids, cancellationToken);
            foreach (var id in request.Ids)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("QualityProfile", id, cancellationToken);
            }
            return Results.NoContent();
        });

        qualityModel.MapGet(string.Empty, async (
            [FromServices] IQualityModelService service,
            CancellationToken cancellationToken) =>
        {
            var model = await service.GetAsync(cancellationToken);
            return Results.Ok(model);
        });

        qualityModel.MapPut(string.Empty, async (
            HttpContext httpContext,
            [FromBody] UpdateQualityModelRequest request,
            [FromServices] IQualityModelService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            try
            {
                var model = await service.SaveAsync(request, cancellationToken);
                return Results.Ok(model);
            }
            catch (InvalidOperationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["qualityModel"] = [ex.Message]
                });
            }
        });

        var qualityPresets = endpoints.MapGroup("/api/quality-profile-presets");

        qualityPresets.MapGet(string.Empty, () =>
        {
            var items = QualityProfilePresetCatalog.All.Select(p => new QualityProfilePresetItem(
                p.Id, p.Name, p.Description, p.MediaType, p.CutoffQuality,
                p.AllowedQualities, p.UpgradeUntilCutoff, p.UpgradeUnknownItems, p.Version));
            return Results.Ok(items);
        });

        qualityPresets.MapPost("{presetId}/apply", async (
            string presetId,
            [FromBody] ApplyQualityPresetRequest request,
            HttpContext httpContext,
            [FromServices] IQualityRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (QualityProfilePresetCatalog.FindById(presetId) is null)
            {
                return Results.NotFound();
            }

            var item = await repository.CreateQualityProfileFromPresetAsync(presetId, request.Name, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("QualityProfile", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        customFormats.MapGet(string.Empty, async ([FromServices] IQualityRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListCustomFormatsAsync(cancellationToken);
            return Results.Ok(items);
        });

        customFormats.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateCustomFormatRequest request,
            [FromServices] IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateCustomFormat(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateCustomFormatAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        customFormats.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateCustomFormatRequest request,
            [FromServices] IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateCustomFormat(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateCustomFormatAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        customFormats.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] IQualityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteCustomFormatAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        policySets.MapGet(string.Empty, async ([FromServices] IQualityRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListPolicySetsAsync(cancellationToken);
            return Results.Ok(items);
        });

        policySets.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreatePolicySetRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IPolicySetLibraryApplier libraryApplier,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidatePolicySet(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreatePolicySetAsync(request, cancellationToken);
            await libraryApplier.ApplyToAssignedLibrariesAsync(item.Id, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("PolicySet", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        policySets.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdatePolicySetRequest request,
            [FromServices] IQualityRepository repository,
            [FromServices] IPolicySetLibraryApplier libraryApplier,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidatePolicySet(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdatePolicySetAsync(id, request, cancellationToken);
            if (item is not null)
            {
                await libraryApplier.ApplyToAssignedLibrariesAsync(item.Id, cancellationToken);
                await realtimeEventPublisher.PublishEntityChangedAsync("PolicySet", item.Id, cancellationToken);
            }
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        policySets.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] IQualityRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeletePolicySetAsync(id, cancellationToken);
            if (removed)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("PolicySet", id, cancellationToken);
            }
            return removed ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> ValidateQualityProfile(CreateQualityProfileRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this quality profile a name."];
        }

        var mediaType = request.MediaType?.Trim().ToLowerInvariant();
        if (mediaType is not ("movies" or "tv" or "tv shows" or "tvshows"))
        {
            errors["mediaType"] = ["Choose whether this profile is for Movies or TV Shows."];
        }

        if (string.IsNullOrWhiteSpace(request.CutoffQuality))
        {
            errors["cutoffQuality"] = ["Choose the quality Deluno should aim for."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateQualityProfile(UpdateQualityProfileRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this quality profile a name."];
        }

        if (string.IsNullOrWhiteSpace(request.CutoffQuality))
        {
            errors["cutoffQuality"] = ["Choose the quality Deluno should aim for."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateCustomFormat(string? name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this custom format a name."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidatePolicySet(string? name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this policy set a name."];
        }

        return errors;
    }
}
