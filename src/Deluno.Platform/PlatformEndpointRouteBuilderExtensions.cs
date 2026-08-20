using Microsoft.AspNetCore.Mvc;
using Deluno.Contracts;
using Deluno.Quality.Presets;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Resilience;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migration;
using Deluno.Platform.Processing;
using Deluno.Quality;
using Deluno.Notifications;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Realtime;
using Deluno.Security.Contracts;
using Deluno.Security;
using System.Net.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Quality.Contracts;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Platform;

public static class PlatformEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var write = endpoints.MapGroup(string.Empty)
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);
        var settings = write.MapGroup("/api/settings");

        settings.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var snapshot = await repository.GetAsync(cancellationToken);
            return Results.Ok(snapshot);
        });

        settings.MapPut(string.Empty, async (
            HttpContext httpContext,
            [FromBody] UpdatePlatformSettingsRequest request,
            IPlatformSettingsRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateSettings(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var snapshot = await repository.SaveAsync(request, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Settings", "settings", cancellationToken);
            return Results.Ok(snapshot);
        });

        settings.MapPut("/automation", async (
            HttpContext httpContext,
            [FromBody] UpdateGlobalAutomationRequest request,
            IPlatformSettingsRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var snapshot = await repository.SetGlobalAutomationEnabledAsync(request.IsEnabled, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Settings", "settings", cancellationToken);
            return Results.Ok(snapshot);
        });

        var tags = write.MapGroup("/api/tags");

        var setup = write.MapGroup("/api/setup");

        setup.MapGet("/progress", async (
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.GetSetupProgressAsync(cancellationToken));
        });

        setup.MapPut("/progress", async (
            HttpContext httpContext,
            [FromBody] UpdateSetupProgressRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.SaveSetupProgressAsync(request, cancellationToken));
        });

        setup.MapGet("/draft", async (
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.GetSetupDraftAsync(cancellationToken));
        });

        setup.MapPut("/draft", async (
            HttpContext httpContext,
            [FromBody] UpdateSetupDraftRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.SaveSetupDraftAsync(request, cancellationToken));
        });

        setup.MapDelete("/draft", async (
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await repository.ClearSetupDraftAsync(cancellationToken);
            return Results.NoContent();
        });

        setup.MapPost("/completed", async (
            HttpContext httpContext,
            [FromBody] SetupCompletedRequest request,
            IPlatformSettingsRepository repository,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var details = JsonSerializer.Serialize(new
            {
                libraries = request.Libraries ?? [],
                qualityProfiles = request.QualityProfiles ?? [],
                customFormatCount = request.CustomFormatCount,
                indexerName = request.IndexerName,
                clientName = request.ClientName,
                firstTitle = request.FirstTitle
            });

            var activity = await activityFeedRepository.RecordActivityAsync(
                "system",
                "Guided setup completed.",
                details,
                null,
                "setup",
                "guided",
                cancellationToken);

            await repository.SaveSetupProgressAsync(
                new UpdateSetupProgressRequest(4, IsCompleted: true),
                cancellationToken);

            return Results.Ok(activity);
        });

        tags.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListTagsAsync(cancellationToken);
            return Results.Ok(items);
        });

        tags.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateTagRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateTag(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateTagAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        tags.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateTagRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateTag(request.Name);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateTagAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        tags.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteTagAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> ValidateSettings(UpdatePlatformSettingsRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.AppInstanceName))
        {
            errors["appInstanceName"] = ["A library name is required."];
        }

        if (request.HostPort <= 0)
        {
            errors["hostPort"] = ["Choose a valid port number."];
        }

        if (!string.IsNullOrWhiteSpace(request.SearchScoringMode) &&
            !SearchScoringModes.IsSupported(request.SearchScoringMode))
        {
            errors["searchScoringMode"] = ["Choose one of: hybrid, rules-only, or ml-only."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateTag(string? name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this tag a name."];
        }

        return errors;
    }

}

public sealed record SetupCompletedRequest(
    IReadOnlyList<string>? Libraries,
    IReadOnlyList<string>? QualityProfiles,
    int CustomFormatCount,
    string? IndexerName,
    string? ClientName,
    string? FirstTitle);

