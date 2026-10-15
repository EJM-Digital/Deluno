using System.Collections.Concurrent;
using System.Text.Json;
using Deluno.Api.Backup;
using Deluno.Api.Updates;
using Deluno.Intake.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform;
using Deluno.Security;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Worker.ApiParity;

/// <summary>
/// What Radarr and Sonarr call a command, in Deluno's own shape (#1019).
///
/// <para>Verified against Radarr's <c>CommandController</c> (Radarr.Api.V3):
/// <c>POST</c> takes <c>{"name": "..."}</c> plus that command's own fields and
/// answers <b>201 Created</b> with the command as it now stands; <c>GET</c>
/// lists them; <c>GET /{id}</c> reads one; <c>DELETE /{id}</c> cancels one. The
/// name is matched case-insensitively. This is the same, except that the id is
/// text (Deluno's job ids are), an unknown name is a <b>400</b> naming the
/// commands it knows, and there is no cancel: work the job queue has already
/// queued is left to run.</para>
///
/// <para>Nothing here does the work itself. Each command hands over to the
/// service that already does it, so a command and the button on screen cannot
/// disagree about what "check feeds" means.</para>
/// </summary>
public sealed record CommandResource(
    string Id,
    string Name,
    string CommandName,
    string? Message,
    JsonElement? Body,
    string Priority,
    string Status,
    string Result,
    DateTimeOffset Queued,
    DateTimeOffset? Started,
    DateTimeOffset? Ended,
    string? Duration,
    string? Exception,
    string Trigger,
    DateTimeOffset? StateChangeTime,
    bool SendUpdatesToClient,
    bool UpdateScheduledTask,
    IReadOnlyList<string> JobIds);

/// <summary>The commands Deluno answers to, and the API-key scope each needs.</summary>
public static class CommandNames
{
    public const string Backup = "Backup";
    public const string CheckForUpdate = "CheckForUpdate";
    public const string RssSync = "RssSync";
    public const string RefreshMonitoredDownloads = "RefreshMonitoredDownloads";
    public const string RefreshMovie = "RefreshMovie";
    public const string RefreshSeries = "RefreshSeries";
    public const string MissingMoviesSearch = "MissingMoviesSearch";
    public const string MissingEpisodeSearch = "MissingEpisodeSearch";

    /// <summary>Canonical name, the scope that may run it, and other names it answers to (Radarr's and Sonarr's).</summary>
    public static readonly IReadOnlyList<(string Name, string Scope, string[] Aliases)> All =
    [
        (Backup, "system", []),
        (CheckForUpdate, "system", ["ApplicationUpdate"]),
        (RssSync, "write", []),
        (RefreshMonitoredDownloads, "queue", []),
        (RefreshMovie, "write", []),
        (RefreshSeries, "write", []),
        (MissingMoviesSearch, "write", []),
        (MissingEpisodeSearch, "write", ["MissingSeriesSearch"])
    ];

    public static (string Name, string Scope)? Resolve(string? name)
    {
        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            return null;
        }

        foreach (var entry in All)
        {
            if (string.Equals(entry.Name, wanted, StringComparison.OrdinalIgnoreCase) ||
                entry.Aliases.Any(alias => string.Equals(alias, wanted, StringComparison.OrdinalIgnoreCase)))
            {
                return (entry.Name, entry.Scope);
            }
        }

        return null;
    }
}

/// <summary>Remembers the commands asked for since the app started, and no more than <see cref="Capacity"/> of them.</summary>
public sealed class CommandLog
{
    public const int Capacity = 100;

    private readonly ConcurrentQueue<CommandRecord> records = new();

    public void Add(CommandRecord record)
    {
        records.Enqueue(record);
        while (records.Count > Capacity && records.TryDequeue(out _))
        {
        }
    }

    public CommandRecord? Find(string id)
        => records.FirstOrDefault(record => string.Equals(record.Id, id, StringComparison.Ordinal));

    public IReadOnlyList<CommandRecord> Recent() => records.ToArray();
}

public sealed record CommandRecord(
    string Id,
    string Name,
    JsonElement? Body,
    DateTimeOffset Queued,
    string Message,
    IReadOnlyList<string> JobIds,
    DateTimeOffset? EndedUtc,
    string? Failure);

public sealed class CommandService(
    CommandLog log,
    IJobQueueRepository jobs,
    IJobScheduler scheduler,
    IActivityFeedRepository activity,
    IDelunoBackupService backups,
    IUpdateOrchestrator updates,
    IDownloadDispatchPollingService dispatchPolling,
    IIntakeRepository intake,
    ILibrariesRepository libraries,
    IMovieCatalogRepository movies,
    ISeriesCatalogRepository series,
    TimeProvider timeProvider)
{
    public async Task<CommandRecord> RunAsync(string name, JsonElement? body, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var id = Guid.NewGuid().ToString("n");
        var jobIds = new List<string>();
        string message;
        string? failure = null;

        try
        {
            message = await DoAsync(name, body, jobIds, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message = $"Deluno could not run {name}.";
            failure = ex.Message;
        }

        await activity.RecordActivityAsync(
            "integration",
            failure is null
                ? $"An outside program asked Deluno to run {name}. {message}"
                : $"An outside program asked Deluno to run {name}, but it could not: {failure}",
            JsonSerializer.Serialize(new { command = name, commandId = id, jobIds }),
            null,
            "integration",
            "command",
            cancellationToken);

        var record = new CommandRecord(
            id,
            name,
            body,
            now,
            message,
            jobIds,
            jobIds.Count == 0 || failure is not null ? timeProvider.GetUtcNow() : null,
            failure);
        log.Add(record);
        return record;
    }

    private async Task<string> DoAsync(string name, JsonElement? body, List<string> jobIds, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case CommandNames.Backup:
            {
                var backup = await backups.CreateBackupAsync("api-command", cancellationToken);
                return $"Backup created: {backup.FileName}.";
            }
            case CommandNames.CheckForUpdate:
            {
                return Deluno.Api.Backup.UpdateCheckWording.Say(await updates.CheckForUpdatesAsync(cancellationToken));
            }
            case CommandNames.RefreshMonitoredDownloads:
            {
                var report = await dispatchPolling.PollAsync(cancellationToken);
                return $"Checked {report.UnresolvedDispatchesChecked} download{(report.UnresolvedDispatchesChecked == 1 ? string.Empty : "s")} with the download clients.";
            }
            case CommandNames.RssSync:
            {
                var sources = (await intake.ListIntakeSourcesAsync(cancellationToken)).Where(source => source.IsEnabled).ToArray();
                foreach (var source in sources)
                {
                    var job = await scheduler.EnqueueAsync(
                        new EnqueueJobRequest(
                            JobType: "intake.sync",
                            Source: "intake",
                            PayloadJson: JsonSerializer.Serialize(new { sourceId = source.Id, source.Name, manual = true }),
                            RelatedEntityType: "intake-source",
                            RelatedEntityId: source.Id,
                            IdempotencyKey: $"intake.sync.manual:{source.Id}:{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}",
                            DedupeKey: $"intake.sync:{source.Id}"),
                        cancellationToken);
                    jobIds.Add(job.Id);
                }

                return sources.Length == 0
                    ? "There are no lists or feeds switched on to check."
                    : $"Checking {sources.Length} list{(sources.Length == 1 ? string.Empty : "s")} now.";
            }
            case CommandNames.RefreshMovie:
            {
                var ids = ReadIds(body, "movieIds", "movieId");
                if (ids.Count == 0)
                {
                    var marked = await movies.RequestMetadataRefreshForAllAsync(cancellationToken);
                    return $"Marked {marked} film{(marked == 1 ? string.Empty : "s")} for a details refresh.";
                }

                foreach (var movieId in ids)
                {
                    var movie = await movies.GetByIdAsync(movieId, cancellationToken)
                        ?? throw new InvalidOperationException($"There is no film with id {movieId}.");
                    var job = await scheduler.EnqueueAsync(
                        new EnqueueJobRequest(
                            "movies.metadata.refresh",
                            "metadata",
                            JsonSerializer.Serialize(new { movie.Id, movie.Title, movie.ReleaseYear }),
                            "movie",
                            movie.Id),
                        cancellationToken);
                    jobIds.Add(job.Id);
                }

                return $"Refreshing {ids.Count} film{(ids.Count == 1 ? string.Empty : "s")}.";
            }
            case CommandNames.RefreshSeries:
            {
                var ids = ReadIds(body, "seriesIds", "seriesId");
                if (ids.Count == 0)
                {
                    var marked = await series.RequestMetadataRefreshForAllAsync(cancellationToken);
                    return $"Marked {marked} show{(marked == 1 ? string.Empty : "s")} for a details refresh.";
                }

                foreach (var seriesId in ids)
                {
                    var item = await series.GetByIdAsync(seriesId, cancellationToken)
                        ?? throw new InvalidOperationException($"There is no show with id {seriesId}.");
                    var job = await scheduler.EnqueueAsync(
                        new EnqueueJobRequest(
                            "series.metadata.refresh",
                            "metadata",
                            JsonSerializer.Serialize(new { item.Id, item.Title, item.StartYear }),
                            "series",
                            item.Id),
                        cancellationToken);
                    jobIds.Add(job.Id);
                }

                return $"Refreshing {ids.Count} show{(ids.Count == 1 ? string.Empty : "s")}.";
            }
            case CommandNames.MissingMoviesSearch:
            case CommandNames.MissingEpisodeSearch:
            {
                var mediaType = name == CommandNames.MissingMoviesSearch ? "movies" : "tv";
                var selected = (await libraries.ListLibrariesAsync(cancellationToken))
                    .Where(library => string.Equals(library.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var library in selected)
                {
                    await jobs.RequestLibrarySearchAsync(
                        LibraryActionEndpointRouteBuilderExtensions.ToPlanItem(library), cancellationToken);
                }

                return $"Started a search in {selected.Length} librar{(selected.Length == 1 ? "y" : "ies")}.";
            }
            default:
                throw new InvalidOperationException($"{name} is not a command Deluno runs.");
        }
    }

    /// <summary>Reads an array field or a single-value field from the body; both may be sent.</summary>
    private static IReadOnlyList<string> ReadIds(JsonElement? body, string arrayName, string singleName)
    {
        var found = new List<string>();
        if (body is not { ValueKind: JsonValueKind.Object } root)
        {
            return found;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, arrayName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Array)
            {
                found.AddRange(property.Value.EnumerateArray().Select(ScalarText).OfType<string>());
            }
            else if (string.Equals(property.Name, singleName, StringComparison.OrdinalIgnoreCase) &&
                     ScalarText(property.Value) is { } single)
            {
                found.Add(single);
            }
        }

        return found.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? ScalarText(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() is { Length: > 0 } text ? text : null,
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };

    /// <summary>The command as its caller sees it now: job-backed ones read their jobs' present state.</summary>
    public async Task<CommandResource> ToResourceAsync(CommandRecord record, CancellationToken cancellationToken)
    {
        var status = "completed";
        var result = "successful";
        DateTimeOffset? started = null;
        var ended = record.EndedUtc;
        var failure = record.Failure;

        if (failure is not null)
        {
            status = "failed";
            result = "unsuccessful";
        }
        else if (record.JobIds.Count > 0)
        {
            var current = new List<JobQueueItem>();
            foreach (var jobId in record.JobIds)
            {
                if (await jobs.GetAsync(jobId, cancellationToken) is { } job)
                {
                    current.Add(job);
                }
            }

            started = current.Select(job => job.StartedUtc).Where(value => value.HasValue).Min();
            var failedJob = current.FirstOrDefault(job => job.Status is "failed" or "dead-letter");
            if (failedJob is not null)
            {
                status = "failed";
                result = "unsuccessful";
                failure = failedJob.LastError ?? "The work failed.";
                ended = failedJob.CompletedUtc ?? timeProvider.GetUtcNow();
            }
            else if (current.Count == record.JobIds.Count && current.All(job => job.Status == "completed"))
            {
                ended = current.Max(job => job.CompletedUtc);
            }
            else
            {
                status = current.Any(job => job.Status == "running") || started.HasValue ? "started" : "queued";
                result = "unknown";
                ended = null;
            }
        }

        return new CommandResource(
            record.Id,
            record.Name,
            record.Name,
            record.Message,
            record.Body,
            "normal",
            status,
            result,
            record.Queued,
            started ?? (status == "completed" ? record.Queued : null),
            ended,
            ended.HasValue ? (ended.Value - (started ?? record.Queued)).ToString("c") : null,
            failure,
            "manual",
            started ?? ended,
            true,
            false,
            record.JobIds);
    }
}

public static class CommandEndpoints
{
    public static IServiceCollection AddDelunoCommands(this IServiceCollection services)
    {
        services.AddSingleton<CommandLog>();
        services.AddScoped<CommandService>();
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var commands = endpoints.MapGroup("/api/command")
            .RequireAuthorization(DelunoAuthorizationPolicies.Commands);

        commands.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] JsonElement body,
            [FromServices] CommandService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            string? requested = null;
            if (body.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in body.EnumerateObject())
                {
                    if (string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        requested = property.Value.GetString();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(requested))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["Say which command to run."]
                });
            }

            var command = CommandNames.Resolve(requested);
            if (command is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = [$"Deluno does not run a command called \"{requested}\". It runs: {string.Join(", ", CommandNames.All.Select(entry => entry.Name))}."]
                });
            }

            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, command.Value.Scope);
            if (scopeDenied is not null)
            {
                return scopeDenied;
            }

            var record = await service.RunAsync(command.Value.Name, body, cancellationToken);
            var resource = await service.ToResourceAsync(record, cancellationToken);
            return Results.Created($"/api/command/{record.Id}", resource);
        });

        commands.MapGet(string.Empty, async (
            HttpContext httpContext,
            [FromServices] CommandLog log,
            [FromServices] CommandService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var resources = new List<CommandResource>();
            foreach (var record in log.Recent().Reverse())
            {
                resources.Add(await service.ToResourceAsync(record, cancellationToken));
            }

            return Results.Ok(resources);
        });

        commands.MapGet("/{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] CommandLog log,
            [FromServices] CommandService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            return log.Find(id) is { } record
                ? Results.Ok(await service.ToResourceAsync(record, cancellationToken))
                : Results.NotFound();
        });

        return endpoints;
    }
}
