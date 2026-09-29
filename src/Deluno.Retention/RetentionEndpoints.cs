using System.Collections.Concurrent;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
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
        services.AddScoped<RetentionCollectionService>();
        services.AddScoped<IRetentionSeerr, RetentionSeerrService>();
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
                .Select(field => new RetentionField(field.Id, field.Label, field.Hint, field.Group, field.ValueKind, field.Operators, field.Options, field.Source, field.UnsupportedOn ?? [], field.PerPerson));
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

        group.MapGet("rules", async ([FromServices] IRetentionRepository repository, CancellationToken cancellationToken)
            => Results.Ok(await repository.ListRulesAsync(cancellationToken)));

        group.MapPost("rules", async (
            [FromBody] SaveRetentionRuleRequest request,
            [FromServices] IRetentionRepository repository,
            [FromServices] IMediaServerRepository mediaServers,
            [FromServices] ISeerrRepository seerrServers,
            CancellationToken cancellationToken) =>
        {
            var problems = RetentionEvaluator.Validate(request, await mediaServers.ListAsync(cancellationToken), (await seerrServers.ListAsync(cancellationToken)).Count(server => server.IsEnabled));
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
            [FromServices] ISeerrRepository seerrServers,
            [FromServices] RetentionCollectionService collections,
            [FromServices] RetentionService service,
            CancellationToken cancellationToken) =>
        {
            var problems = RetentionEvaluator.Validate(request, await mediaServers.ListAsync(cancellationToken), (await seerrServers.ListAsync(cancellationToken)).Count(server => server.IsEnabled));
            if (problems.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rule"] = problems.ToArray() });
            }

            var before = await repository.GetRuleAsync(id, cancellationToken);
            var saved = await repository.SaveRuleAsync(id, request, cancellationToken);
            if (saved is null)
            {
                return Results.NotFound();
            }

            // A titles whose day already came under the old action is done afresh, after a new wait, under the new one.
            if (before is not null && (before.ExpiryAction != saved.ExpiryAction || !string.Equals(before.ExpiryTag, saved.ExpiryTag, StringComparison.OrdinalIgnoreCase)))
            {
                await service.ForgetDoneAsync(saved.Id, cancellationToken);
            }

            // Turning a rule off or changing its servers changes what the collections hold.
            await collections.TrySyncAsync(cancellationToken);
            return Results.Ok(saved);
        });

        group.MapDelete("rules/{id}", async (string id, [FromServices] IRetentionRepository repository, [FromServices] RetentionCollectionService collections, CancellationToken cancellationToken) =>
        {
            if (!await repository.DeleteRuleAsync(id, cancellationToken))
            {
                return Results.NotFound();
            }

            await collections.TrySyncAsync(cancellationToken);
            return Results.NoContent();
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
                "trial", request.Name ?? "Trial", level, true,
                request.Steps ?? RetentionStepList.FromConditions(request.Conditions ?? []),
                request.MediaServerIds ?? [], request.RemoveFromServers,
                request.WaitDays ?? RetentionLimits.DefaultWaitDays, RetentionRemovalModes.Normalize(request.RemovalMode),
                request.CheckIntervalHours ?? RetentionLimits.DefaultCheckIntervalHours, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                RetentionExpiryActions.Normalize(request.ExpiryAction), request.ExpiryTag?.Trim(), request.KeepInDelunoOnly ?? false, request.DeferWhilePlaying ?? false);
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
                evaluation.Unmanaged ?? [],
                rule.ExpiryAction,
                RetentionExpiryActions.Describe(rule.ExpiryAction, rule.ExpiryTag)));
        });

        // Titles a person can add to a rule's list by hand (#1008): what Deluno holds files for at the rule's level.
        group.MapGet("rules/{id}/titles", async (
            string id,
            string? query,
            [FromServices] IRetentionRepository repository,
            [FromServices] RetentionEvaluator evaluator,
            CancellationToken cancellationToken) =>
        {
            var rule = await repository.GetRuleAsync(id, cancellationToken);
            if (rule is null)
            {
                return Results.NotFound();
            }

            var listed = (await repository.ListMarksAsync(rule.Id, cancellationToken)).Select(mark => mark.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kept = (await repository.ListKeepsAsync(cancellationToken))
                .Where(keep => keep.RuleId is null || string.Equals(keep.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase))
                .Select(keep => keep.EntityId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = await evaluator.SearchTitlesAsync(rule.Level, query, 20, cancellationToken);
            return Results.Ok(found.Select(unit => new RetentionAddable(
                unit.EntityId,
                unit.Level,
                unit.Level is RetentionLevels.Season or RetentionLevels.Episode ? $"{unit.ShowTitle} · {unit.Title}" : unit.Title,
                unit.Year,
                listed.Contains(unit.EntityId),
                kept.Contains(unit.EntityId))).ToArray());
        });

        group.MapPost("rules/{id}/titles", async (
            string id,
            [FromBody] AddRetentionTitleRequest request,
            [FromServices] IRetentionRepository repository,
            [FromServices] RetentionService service,
            [FromServices] RetentionCollectionService collections,
            CancellationToken cancellationToken) =>
        {
            var rule = await repository.GetRuleAsync(id, cancellationToken);
            if (rule is null)
            {
                return Results.NotFound();
            }

            var added = await service.AddManualAsync(rule, request.EntityId, cancellationToken);
            if (added.Mark is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [added.Problem ?? "It could not be added."] });
            }

            await collections.TrySyncAsync(cancellationToken);
            return Results.Ok(added.Mark);
        });

        // Postpone: push a removal day back N days without keeping the title (#1010).
        group.MapPost("leaving-soon/postpone", async (
            [FromBody] PostponeRetentionRequest request,
            [FromServices] IRetentionRepository repository,
            [FromServices] RetentionService service,
            CancellationToken cancellationToken) =>
        {
            var rule = string.IsNullOrWhiteSpace(request.RuleId) ? null : await repository.GetRuleAsync(request.RuleId, cancellationToken);
            if (rule is null)
            {
                return Results.NotFound();
            }

            if (RetentionLimits.PostponeProblem(request.Days) is { } problem)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = [problem] });
            }

            return Results.Ok(await service.PostponeAsync(rule, request.EntityIds ?? [], request.Days!.Value, cancellationToken));
        });

        // Run now: the same check the schedule runs.
        group.MapPost("rules/{id}/check", async (
            string id,
            [FromServices] IRetentionRepository repository,
            [FromServices] RetentionService service,
            [FromServices] RetentionCollectionService collections,
            [FromServices] IRetentionSeerr seerr,
            CancellationToken cancellationToken) =>
        {
            var rule = await repository.GetRuleAsync(id, cancellationToken);
            if (rule is null)
            {
                return Results.NotFound();
            }

            var run = await service.CheckAsync(rule, "person", cancellationToken);
            await collections.TrySyncAsync(cancellationToken);
            await seerr.RetryAsync(cancellationToken);
            return Results.Ok(run);
        });

        group.MapGet("rules/{id}/runs", async (string id, int? take, [FromServices] IRetentionRepository repository, CancellationToken cancellationToken)
            => Results.Ok(await repository.ListRunsAsync(id, take ?? 20, cancellationToken)));

        group.MapGet("runs/{runId}", async (string runId, [FromServices] IRetentionRepository repository, CancellationToken cancellationToken)
            => await repository.GetRunAsync(runId, cancellationToken) is { } run ? Results.Ok(run) : Results.NotFound());

        group.MapGet("leaving-soon", async ([FromServices] IRetentionRepository repository, CancellationToken cancellationToken)
            => Results.Ok(await repository.ListMarksAsync(null, cancellationToken)));

        // Ready to remove, confirmed. The rule is asked again before anything goes.
        group.MapPost("leaving-soon/remove", async (
            [FromBody] RetentionConfirmRequest request,
            [FromServices] IRetentionRepository repository,
            [FromServices] RetentionService service,
            [FromServices] RetentionCollectionService collections,
            CancellationToken cancellationToken) =>
        {
            var rule = string.IsNullOrWhiteSpace(request.RuleId) ? null : await repository.GetRuleAsync(request.RuleId, cancellationToken);
            if (rule is null)
            {
                return Results.NotFound();
            }

            var run = await service.ConfirmAsync(rule, request.EntityIds ?? [], cancellationToken);
            await collections.TrySyncAsync(cancellationToken);
            return Results.Ok(run);
        });

        group.MapGet("keeps", async ([FromServices] IRetentionRepository repository, CancellationToken cancellationToken)
            => Results.Ok(await repository.ListKeepsAsync(cancellationToken)));

        group.MapPost("keeps", async ([FromBody] SaveRetentionKeepRequest request, [FromServices] RetentionService service, [FromServices] RetentionCollectionService collections, CancellationToken cancellationToken) =>
        {
            var kept = await service.KeepAsync(request, cancellationToken);
            if (kept is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["keep"] = ["Choose a title to keep."] });
            }

            // A kept title leaves the Leaving soon collections now, not at the next check.
            await collections.TrySyncAsync(cancellationToken);
            return Results.Ok(kept);
        });

        // Undoing a keep does not mark the title again; the rule's next check does.
        group.MapDelete("keeps/{id}", async (string id, [FromServices] IRetentionRepository repository, CancellationToken cancellationToken)
            => await repository.DeleteKeepAsync(id, cancellationToken) is null ? Results.NotFound() : Results.NoContent());

        // Titles a rule removed that Seerr has not been cleared of yet, and why (#522).
        group.MapGet("seerr-cleanups", async ([FromServices] IRetentionSeerr seerr, CancellationToken cancellationToken)
            => Results.Ok(await seerr.ListCleanupsAsync(cancellationToken)));

        // Each server's Leaving soon collections, and why the last update failed if it did.
        group.MapGet("collections", async ([FromServices] IRetentionRepository repository, CancellationToken cancellationToken)
            => Results.Ok(await repository.ListCollectionStatesAsync(cancellationToken)));

        group.MapGet("titles/{entityId}", async (string entityId, [FromServices] RetentionService service, CancellationToken cancellationToken)
            => Results.Ok(await service.TitleStatusAsync(entityId, cancellationToken)));

        group.MapDelete("episode-removals/{episodeId}", async (string episodeId, [FromServices] RetentionService service, CancellationToken cancellationToken)
            => await service.UndoEpisodeRemovalAsync(episodeId, cancellationToken) is null ? Results.NotFound() : Results.NoContent());

        return endpoints;
    }
}

/// <summary>
/// Checks each enabled rule when its interval has passed (#494). A check marks
/// what matches as Leaving soon and removes only what is past its wait and
/// still matches.
/// </summary>
public sealed class RetentionScheduleHostedService(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<RetentionScheduleHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

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
        var checkedAny = false;
        foreach (var rule in await repository.ListRulesAsync(cancellationToken))
        {
            if (rule.NextCheckUtc is not { } due || due > now)
            {
                continue;
            }

            checkedAny = true;

            try
            {
                await service.CheckAsync(rule, "schedule", cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One rule's failure does not stop the others being checked.
                logger.LogWarning(exception, "Retention rule {Rule} could not be checked.", rule.Name);
            }
        }

        // After the checks, so the collections show what they decided; a server
        // that failed last time is caught up here.
        if (checkedAny)
        {
            await scope.ServiceProvider.GetRequiredService<IRetentionSeerr>().RetryAsync(cancellationToken);
            await scope.ServiceProvider.GetRequiredService<RetentionCollectionService>().TrySyncAsync(cancellationToken);
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
