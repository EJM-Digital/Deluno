using Deluno.Connections.Data;
using System.Collections.Concurrent;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
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
        services.AddSingleton<IRetentionDiskSpace, RetentionDiskSpace>();
        services.AddSingleton<RetentionLibraryCache>();
        services.AddScoped<RetentionUnitReader>();
        services.AddScoped<RetentionEvaluator>();
        services.AddScoped<RetentionService>();
        services.AddHostedService<RetentionScheduleHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoRetention(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/retention").RequireAuthorization(DelunoAuthorizationPolicies.Write);

        group.MapGet("fields", (string? level, string? mediaType) =>
        {
            var at = RetentionLevels.Normalize(level, mediaType);
            var kind = RetentionFieldCatalog.CatalogueKind(at);
            var retention = RetentionFieldCatalog.Fields
                .Where(field => field.Levels.Contains(at))
                .Select(field => new RetentionField(field.Id, field.Label, field.Hint, field.Group, field.ValueKind, field.Operators, null, field.Source, field.UnsupportedOn ?? []));
            // A season or episode is asked its show's catalogue questions.
            var catalogue = CatalogueFilterFields.For(kind).Select(field => new RetentionField(
                field.Id,
                field.Label,
                field.Hint,
                // The casing /api/movies/controls uses, so the browser reads one vocabulary.
                System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(field.Group.ToString()),
                System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(field.ValueKind.ToString()),
                field.Operators.Select(CatalogueFilterOperators.Token).ToArray(),
                field.Options,
                RetentionFieldSources.Deluno,
                []));
            return Results.Ok(retention.Concat(catalogue).ToArray());
        });

        // The builder's live counts: what the rule as drawn would select now.
        // Nothing is saved, and server listings may be a few minutes old.
        group.MapPost("rules/trial", async (
            [FromBody] SaveRetentionRuleRequest request,
            [FromServices] RetentionEvaluator evaluator,
            [FromServices] RetentionLibraryCache cache,
            [FromServices] IMediaServerClientRegistry clients,
            CancellationToken cancellationToken) =>
        {
            var level = RetentionLevels.Normalize(request.Level, request.MediaType);
            var rule = new RetentionRule(
                "trial", request.Name ?? "Trial", RetentionLevels.MediaType(level), true, request.MediaServerIds ?? [], request.RemoveFromServers,
                RetentionScheduleModes.Manual, 24, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, level,
                request.Steps ?? RetentionStepList.FromConditions(request.Conditions ?? []));
            var evaluation = await evaluator.EvaluateAsync(rule, cancellationToken, (server, mediaType, token) => cache.GetAsync(server, mediaType, clients, token));
            var matched = evaluation.Matched.ToArray();
            return Results.Ok(new RetentionTrial(
                evaluation.Considered,
                evaluation.StepCounts ?? [],
                matched.Length,
                matched.Sum(item => item.SizeBytes ?? 0),
                matched.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).Take(50)
                    .Select(item => new RetentionRunItem(item.EntityId, item.MediaType, item.Title, item.Year, item.SizeBytes, item.Reasons, item.Outcome, item.Message))
                    .ToArray(),
                evaluation.Candidates.Count(item => item.Outcome == RetentionItemOutcomes.Undecided),
                evaluation.UnreachableServers,
                evaluation.Problems,
                evaluation.Unmanaged ?? []));
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

/// <summary>
/// Server listings kept for a few minutes for the step builder's live counts,
/// so changing one step does not read every library again. Never used by a run
/// that can remove anything.
/// </summary>
public sealed class RetentionLibraryCache(TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, MediaServerAnswer<MediaServerLibrary> Answer)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public async Task<MediaServerAnswer<MediaServerLibrary>> GetAsync(
        MediaServerConnection server,
        string mediaType,
        IMediaServerClientRegistry clients,
        CancellationToken cancellationToken)
    {
        var key = $"{server.Id}|{server.UpdatedUtc:O}|{mediaType}";
        var now = timeProvider.GetUtcNow();
        if (_entries.TryGetValue(key, out var entry) && now - entry.At < Lifetime)
        {
            return entry.Answer;
        }

        var client = clients.Find(server.Kind);
        var answer = client is null
            ? new MediaServerAnswer<MediaServerLibrary>.NotSupported($"{server.Name} is a kind of server this build cannot read.")
            : await client.ListItemsAsync(server, mediaType, cancellationToken);
        // Only an answer is worth keeping; a server that could not be asked is asked again.
        if (answer is MediaServerAnswer<MediaServerLibrary>.Reported)
        {
            _entries[key] = (now, answer);
        }

        return answer;
    }
}
