using System.Text.Json;
using Deluno.Contracts;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Intake;

/// <summary>
/// /api/intake-sources and /api/intake-title-origins. Split out of
/// PlatformEndpointRouteBuilderExtensions by ADR-001 Step 1; handler bodies are
/// unchanged apart from the repository type and explicit [FromServices].
/// </summary>
public static class IntakeEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoIntakeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var intakeSources = endpoints.MapGroup("/api/intake-sources");

        intakeSources.MapGet(string.Empty, async ([FromServices] IIntakeRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListIntakeSourcesAsync(cancellationToken);
            return Results.Ok(items);
        });

        endpoints.MapGet("/api/intake-title-origins", async (
            HttpContext httpContext,
            string? mediaType,
            string? entityId,
            [FromServices] IIntakeRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (string.IsNullOrWhiteSpace(entityId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["entityId"] = ["A title ID is required."] });
            }

            if (!string.Equals(mediaType, "movies", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mediaType"] = ["Media type must be movies or tv."] });
            }

            var normalizedMediaType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movies";
            var items = await repository.ListIntakeTitleOriginsAsync(normalizedMediaType, entityId, cancellationToken);
            return Results.Ok(items);
        });

        intakeSources.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateIntakeSourceRequest request,
            [FromServices] IIntakeRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateIntakeSource(
                request.Name,
                request.Provider,
                request.FeedUrl,
                request.MinimumRating,
                request.MinimumYear,
                request.MaximumAgeDays,
                request.SyncIntervalHours);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateIntakeSourceAsync(request, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("IntakeSource", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        intakeSources.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateIntakeSourceRequest request,
            [FromServices] IIntakeRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateIntakeSource(
                request.Name,
                request.Provider,
                request.FeedUrl,
                request.MinimumRating,
                request.MinimumYear,
                request.MaximumAgeDays,
                request.SyncIntervalHours);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateIntakeSourceAsync(id, request, cancellationToken);
            if (item is null) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("IntakeSource", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        intakeSources.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] IIntakeRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteIntakeSourceAsync(id, cancellationToken);
            if (!removed) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("IntakeSource", id, cancellationToken);
            return Results.NoContent();
        });

        intakeSources.MapPost("{id}/sync", async (
            string id,
            HttpContext httpContext,
            [FromServices] IIntakeRepository repository,
            IJobScheduler jobScheduler,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var source = await repository.GetIntakeSourceAsync(id, cancellationToken);
            if (source is null)
            {
                return Results.NotFound();
            }

            var job = await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "intake.sync",
                    Source: "intake",
                    PayloadJson: JsonSerializer.Serialize(new { sourceId = source.Id, source.Name, manual = true }),
                    RelatedEntityType: "intake-source",
                    RelatedEntityId: source.Id,
                    IdempotencyKey: $"intake.sync.manual:{source.Id}:{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                    DedupeKey: $"intake.sync:{source.Id}"),
                cancellationToken);

            await realtimeEventPublisher.PublishEntityChangedAsync("IntakeSource", source.Id, cancellationToken);

            return Results.Accepted($"/api/jobs/{job.Id}", job);
        });

        intakeSources.MapPost("{id}/preview", async (
            string id,
            HttpContext httpContext,
            IIntakeListPreviewService previewService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            try
            {
                return Results.Ok(await previewService.PreviewAsync(id, cancellationToken));
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });

        intakeSources.MapPost("{id}/approve-preview", async (
            string id,
            HttpContext httpContext,
            [FromBody] ApproveIntakeListPreviewRequest request,
            IIntakeListApprovalService approvalService,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            try
            {
                var result = await approvalService.ApproveAsync(id, request, cancellationToken);
                await realtimeEventPublisher.PublishEntityChangedAsync("IntakeSource", id, cancellationToken);
                return Results.Ok(result);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });

        intakeSources.MapGet("{id}/exclusions", async (
            string id,
            HttpContext httpContext,
            [FromServices] IIntakeRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            return Results.Ok(await repository.ListActiveIntakeListExclusionsAsync(id, cancellationToken));
        });

        intakeSources.MapPost("{id}/exclude-preview", async (
            string id,
            HttpContext httpContext,
            [FromBody] CreateIntakeListExclusionRequest request,
            [FromServices] IIntakeRepository repository,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var source = await repository.GetIntakeSourceAsync(id, cancellationToken);
            if (source is null)
            {
                return Results.NotFound();
            }

            var exclusion = await repository.CreateIntakeListExclusionAsync(id, request, cancellationToken);
            if (exclusion is null)
            {
                return Results.BadRequest(new { message = "An entry title is required to exclude it." });
            }

            await activityFeedRepository.RecordActivityAsync(
                "intake.entry.excluded",
                $"{source.Name}: excluded {exclusion.Title}{(exclusion.Year is null ? string.Empty : $" ({exclusion.Year})")}.",
                JsonSerializer.Serialize(new { SourceId = source.Id, ExclusionId = exclusion.Id, exclusion.EntryKey, exclusion.ExpiresUtc }),
                null,
                "intake-source",
                source.Id,
                cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("IntakeSource", source.Id, cancellationToken);
            return Results.Ok(exclusion);
        });

        intakeSources.MapDelete("{id}/exclusions/{exclusionId}", async (
            string id,
            string exclusionId,
            HttpContext httpContext,
            [FromServices] IIntakeRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteIntakeListExclusionAsync(id, exclusionId, cancellationToken);
            if (!removed) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("IntakeSource", id, cancellationToken);
            return Results.NoContent();
        });

        intakeSources.MapGet("{id}/diagnostics", async (
            string id,
            int? pageSize,
            string? pageToken,
            HttpContext httpContext,
            [FromServices] IIntakeRepository repository,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var source = await repository.GetIntakeSourceAsync(id, cancellationToken);
            if (source is null)
            {
                return Results.NotFound();
            }

            var diagnostics = await activityFeedRepository.ListActivityPageAsync(
                new PageRequest(pageSize ?? 50, pageToken), "intake-source", source.Id, cancellationToken);
            return Results.Ok(new
            {
                source,
                diagnostics
            });
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> ValidateIntakeSource(
        string? name,
        string? provider,
        string? feedUrl,
        double? minimumRating,
        int? minimumYear,
        int? maximumAgeDays,
        int? syncIntervalHours)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this list source a name."];
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            errors["provider"] = ["Choose a provider."];
        }

        var addressError = IntakeSourceAddressValidator.Validate(provider, feedUrl);
        if (!string.IsNullOrWhiteSpace(addressError))
        {
            errors["feedUrl"] = [addressError];
        }

        if (minimumRating is < 0 or > 10)
        {
            errors["minimumRating"] = ["Minimum rating must be between 0 and 10."];
        }

        if (minimumYear is < 1888 or > 2100)
        {
            errors["minimumYear"] = ["Minimum year must be between 1888 and 2100."];
        }

        if (maximumAgeDays is <= 0)
        {
            errors["maximumAgeDays"] = ["Maximum age in days must be greater than zero when provided."];
        }

        if (syncIntervalHours is <= 0 or > 168)
        {
            errors["syncIntervalHours"] = ["Sync interval must be between 1 and 168 hours."];
        }

        return errors;
    }

}
