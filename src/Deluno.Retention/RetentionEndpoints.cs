using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Retention;

public static class RetentionModule
{
    public static IServiceCollection AddDelunoRetentionModule(this IServiceCollection services)
    {
        services.AddSingleton<IRetentionRepository, SqliteRetentionRepository>();
        services.AddScoped<RetentionEvaluator>();
        services.AddScoped<RetentionService>();
        services.AddHostedService<RetentionScheduleHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoRetention(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/retention").RequireAuthorization(DelunoAuthorizationPolicies.Write);

        group.MapGet("fields", (string? mediaType) =>
        {
            var kind = mediaType == "tv" ? MediaKind.Series : MediaKind.Movie;
            var catalogue = CatalogueFilterFields.For(kind).Select(field => new RetentionField(
                field.Id,
                field.Label,
                field.Hint,
                // The casing /api/movies/controls uses, so the browser reads one vocabulary.
                System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(field.Group.ToString()),
                System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(field.ValueKind.ToString()),
                field.Operators.Select(CatalogueFilterOperators.Token).ToArray(),
                field.Options,
                NeedsMediaServer: false));
            return Results.Ok(RetentionWatchFields.Fields.Concat(catalogue).ToArray());
        });

        group.MapGet("rules", async ([FromServices] IRetentionRepository repository, CancellationToken cancellationToken)
            => Results.Ok(await repository.ListRulesAsync(cancellationToken)));

        group.MapPost("rules", async (
            [FromBody] SaveRetentionRuleRequest request,
            [FromServices] IRetentionRepository repository,
            [FromServices] IMediaServerRepository mediaServers,
            CancellationToken cancellationToken) =>
        {
            var problems = RetentionEvaluator.Validate(request, await mediaServers.ListAsync(cancellationToken));
            if (problems.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rule"] = problems.ToArray() });
            }

            var saved = await repository.SaveRuleAsync(null, request, cancellationToken);
            return Results.Created($"/api/retention/rules/{saved!.Id}", saved);
        });

        group.MapPut("rules/{id}", async (
            string id,
            [FromBody] SaveRetentionRuleRequest request,
            [FromServices] IRetentionRepository repository,
            [FromServices] IMediaServerRepository mediaServers,
            CancellationToken cancellationToken) =>
        {
            var problems = RetentionEvaluator.Validate(request, await mediaServers.ListAsync(cancellationToken));
            if (problems.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rule"] = problems.ToArray() });
            }

            var saved = await repository.SaveRuleAsync(id, request, cancellationToken);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        });

        group.MapDelete("rules/{id}", async (string id, [FromServices] IRetentionRepository repository, CancellationToken cancellationToken)
            => await repository.DeleteRuleAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        // A preview is a dry run: it removes nothing, and it is the only thing a
        // cleanup can act on.
        group.MapPost("rules/{id}/preview", async (
            string id,
            [FromServices] IRetentionRepository repository,
            [FromServices] RetentionService service,
            CancellationToken cancellationToken) =>
        {
            var rule = await repository.GetRuleAsync(id, cancellationToken);
            return rule is null ? Results.NotFound() : Results.Ok(await service.PreviewAsync(rule, "person", cancellationToken));
        });

        group.MapGet("rules/{id}/runs", async (string id, int? take, [FromServices] IRetentionRepository repository, CancellationToken cancellationToken)
            => Results.Ok(await repository.ListRunsAsync(id, take ?? 20, cancellationToken)));

        group.MapGet("runs/{runId}", async (string runId, [FromServices] IRetentionRepository repository, CancellationToken cancellationToken)
            => await repository.GetRunAsync(runId, cancellationToken) is { } run ? Results.Ok(run) : Results.NotFound());

        group.MapPost("runs/{runId}/cleanup", async (
            string runId,
            [FromBody] RetentionCleanupRequest? request,
            [FromServices] IRetentionRepository repository,
            [FromServices] RetentionService service,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var preview = await repository.GetRunAsync(runId, cancellationToken);
            if (preview is null)
            {
                return Results.NotFound();
            }

            if (preview.Kind != RetentionRunKinds.Preview)
            {
                return Results.Conflict(new { message = "Only a preview can be cleaned up. Run a preview and choose from what it lists." });
            }

            // A preview is what a person saw. An old one is not.
            if (timeProvider.GetUtcNow() - preview.StartedUtc > TimeSpan.FromHours(24))
            {
                return Results.Conflict(new { message = "This preview is more than a day old. Run a new preview so you see what would be removed now." });
            }

            var rule = await repository.GetRuleAsync(preview.RuleId, cancellationToken);
            if (rule is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(await service.CleanupAsync(rule, preview, request?.EntityIds, "person", cancellationToken));
        });

        return endpoints;
    }
}

/// <summary>
/// Runs rules on their schedules. A rule set to preview only records what it
/// would remove; only a rule explicitly set to clean up removes anything
/// unattended.
/// </summary>
public sealed class RetentionScheduleHostedService(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<RetentionScheduleHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Scheduled retention rules could not run.");
            }

            try
            {
                await Task.Delay(Tick, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task RunDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRetentionRepository>();
        var service = scope.ServiceProvider.GetRequiredService<RetentionService>();
        var now = timeProvider.GetUtcNow();
        foreach (var rule in await repository.ListRulesAsync(cancellationToken))
        {
            if (!rule.IsEnabled || rule.ScheduleMode == RetentionScheduleModes.Manual)
            {
                continue;
            }

            if (rule.LastScheduledRunUtc is { } last && now - last < TimeSpan.FromHours(rule.ScheduleIntervalHours))
            {
                continue;
            }

            await repository.MarkScheduledRunAsync(rule.Id, now, cancellationToken);
            var preview = await service.PreviewAsync(rule, "schedule", cancellationToken);
            if (rule.ScheduleMode == RetentionScheduleModes.Cleanup && preview.MatchedCount > 0)
            {
                await service.CleanupAsync(rule, preview, null, "schedule", cancellationToken);
            }
        }
    }
}
