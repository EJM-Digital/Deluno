using Deluno.Contracts;
using Deluno.Contracts.Manifest;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Api.SystemInfo;

/// <summary>One line of Deluno's own log (#1098): when, how serious, what, and which part of Deluno said it.</summary>
public sealed record SystemLogEntry(string Id, DateTimeOffset TimeUtc, string Level, string Message, string? Source);

public sealed record SystemLogResponse(IReadOnlyList<SystemLogEntry> Entries, int ErrorsToday, int WarningsToday, string? LatestLogFile);

public sealed record SystemDatabaseItem(string Key, string Name, long SizeBytes);

public sealed record SystemDatabasesResponse(
    IReadOnlyList<SystemDatabaseItem> Databases,
    long TotalBytes,
    DateTimeOffset? IntegrityCheckedUtc,
    bool? IntegrityOk,
    string? IntegrityNote,
    DateTimeOffset? CompactedUtc);

/// <summary>
/// What the System screen's Log card and its Backups and databases card read (#1098):
/// <c>GET /api/system/log</c> (today's notable lines, newest first) and
/// <c>GET /api/system/databases</c> (each database's size and when it was last checked and compacted).
/// </summary>
public static class SystemLogAndDatabasesEndpointRouteBuilderExtensions
{
    public const string LogEntryEventName = "SystemLogEntry";

    public static IEndpointRouteBuilder MapDelunoSystemLogAndDatabasesEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/system/log", (
                string? level,
                int? take,
                [FromServices] DelunoLogBuffer buffer,
                IOptions<StoragePathOptions> storage,
                IOptions<DelunoFileLogOptions> logOptions) =>
            {
                var wanted = NormaliseLevel(level);
                if (wanted is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["level"] = ["Choose all, error or warning."]
                    });
                }

                var directory = DelunoFileLoggerProvider.ResolveDirectory(storage.Value.DataRoot, logOptions.Value.Directory);
                buffer.SeedFromFiles(directory);

                var everything = buffer.Recent("all", DelunoLogBuffer.Capacity);
                var entries = buffer.Recent(wanted, take ?? 100).Select(ToItem).ToArray();
                var latest = Directory.Exists(directory)
                    ? new DirectoryInfo(directory)
                        .EnumerateFiles($"{DelunoFileLoggerProvider.FilePrefix}*{DelunoFileLoggerProvider.FileExtension}")
                        .OrderByDescending(file => file.LastWriteTimeUtc)
                        .Select(file => file.Name)
                        .FirstOrDefault()
                    : null;
                return Results.Ok(new SystemLogResponse(
                    entries,
                    everything.Count(entry => entry.Level == "error"),
                    everything.Count(entry => entry.Level == "warning"),
                    latest));
            })
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        api.MapGet("/system/databases", (
                IOptions<StoragePathOptions> storage) =>
            Results.Ok(DescribeDatabases(storage.Value.DataRoot)))
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        return api;
    }

    /// <summary>"all", "error" or "warning"; null when the caller asked for something else.</summary>
    public static string? NormaliseLevel(string? level)
        => string.IsNullOrWhiteSpace(level) || level.Equals("all", StringComparison.OrdinalIgnoreCase) ? "all"
            : level.Equals("error", StringComparison.OrdinalIgnoreCase) || level.Equals("errors", StringComparison.OrdinalIgnoreCase) ? "error"
            : level.Equals("warning", StringComparison.OrdinalIgnoreCase) || level.Equals("warn", StringComparison.OrdinalIgnoreCase) || level.Equals("warnings", StringComparison.OrdinalIgnoreCase) ? "warning"
            : null;

    public static SystemLogEntry ToItem(LogEntryView entry)
        => new(entry.Id, entry.TimeUtc, entry.Level, entry.Message, entry.Source);

    /// <summary>The plain-words name a database goes by on screen.</summary>
    public static string PlainName(string key) => key switch
    {
        "platform" => "Settings and connections",
        "movies" => "Movies",
        "series" => "TV shows",
        "jobs" => "Activity and jobs",
        "cache" => "Metadata cache",
        "downloader" => "Downloads",
        _ => key
    };

    public static SystemDatabasesResponse DescribeDatabases(string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot);
        var items = new List<SystemDatabaseItem>();
        foreach (var database in DelunoSystemManifest.Databases)
        {
            var size = 0L;
            var found = false;
            foreach (var suffix in new[] { string.Empty, "-wal" })
            {
                var path = Path.Combine(root, database.FileName + suffix);
                if (File.Exists(path))
                {
                    found = true;
                    size += new FileInfo(path).Length;
                }
            }

            if (found)
            {
                items.Add(new SystemDatabaseItem(database.Key, PlainName(database.Key), size));
            }
        }

        var maintenance = DatabaseMaintenanceLog.Read(root);
        return new SystemDatabasesResponse(
            items.OrderByDescending(item => item.SizeBytes).ToArray(),
            items.Sum(item => item.SizeBytes),
            maintenance.IntegrityCheckedUtc,
            maintenance.IntegrityOk,
            maintenance.IntegrityNote,
            maintenance.CompactedUtc);
    }
}

/// <summary>
/// Once a day, asks each database whether its file is sound (SQLite's quick check) and notes
/// the result for the System screen; a problem is also logged as a warning so it reaches the
/// Log card. Runs as the "Check the databases" scheduled task.
/// </summary>
public sealed class DatabaseIntegrityService(
    IDelunoDatabaseConnectionFactory databases,
    IJobQueueRepository jobs,
    IHealthCheckRepository health,
    TimeProvider timeProvider,
    ILogger<DatabaseIntegrityService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let start-up (migrations, the first screens) settle before reading every file.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                if (await jobs.TryClaimScheduledPassAsync(
                        SystemTasks.DatabaseIntegrity,
                        SystemTasks.IntervalFor(SystemTasks.DatabaseIntegrity),
                        stoppingToken))
                {
                    await RunOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The daily database check could not run.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Checks every database, records the outcome and returns the problems found (empty when all are sound).</summary>
    public async Task<IReadOnlyList<string>> RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedUtc = timeProvider.GetUtcNow();
        var problems = new List<string>();
        string? failure = null;
        try
        {
            foreach (var database in DelunoSystemManifest.Databases)
            {
                if (!File.Exists(databases.GetDatabasePath(database.Key)))
                {
                    continue;
                }

                await using var connection = await databases.OpenReadOnlyConnectionAsync(database.Key, cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA quick_check;";
                var answer = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "ok";
                if (!answer.Equals("ok", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add(SystemLogAndDatabasesEndpointRouteBuilderExtensions.PlainName(database.Key));
                    logger.LogWarning("The {Database} database did not pass its daily check: {Answer}", database.Key, answer);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = PlainFailure.Describe(exception, "checking the databases");
            logger.LogWarning(exception, "The daily database check could not finish.");
        }

        var completedUtc = timeProvider.GetUtcNow();
        var root = Path.GetDirectoryName(databases.GetDatabasePath(DelunoSystemManifest.Databases[0].Key));
        var ok = failure is null && problems.Count == 0;
        var note = failure ?? (problems.Count == 0 ? null : $"Problem found in: {string.Join(", ", problems)}.");
        if (!string.IsNullOrEmpty(root) && failure is null)
        {
            if (DatabaseMaintenanceLog.RecordIntegrity(root, completedUtc, ok, note) is { } notKept)
            {
                logger.LogWarning("The integrity check ran, but its note was not kept: {Reason}", notKept);
            }
        }

        if (failure is null && problems.Count == 0)
        {
            logger.LogInformation("Database integrity check passed for every database.");
        }

        await jobs.RecordScheduledPassOutcomeAsync(
            SystemTasks.DatabaseIntegrity,
            completedUtc,
            failure is null && problems.Count == 0 ? "completed" : "failed",
            Math.Max(0, (long)(completedUtc - startedUtc).TotalMilliseconds),
            startedUtc.Add(SystemTasks.IntervalFor(SystemTasks.DatabaseIntegrity)),
            CancellationToken.None,
            note);
        // Health reads the stored answers of its checks (#1335): ask for the System ones now, so a damaged database
        // shows within seconds and a mended one clears, not at the next quarter hour.
        try
        {
            await health.RequestImmediateRunAsync(HealthCheckCategories.System, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The database check ran, but Health could not be asked to look again.");
        }

        return problems;
    }
}

/// <summary>
/// Pushes each new notable log line to screens watching the dashboard subject (event
/// <see cref="SystemLogAndDatabasesEndpointRouteBuilderExtensions.LogEntryEventName"/>), so the Log card
/// moves without asking. Nothing is sent while nobody is watching.
/// </summary>
public sealed class SystemLogPushService(
    DelunoLogBuffer buffer,
    IRealtimeEventPublisher publisher,
    IRealtimeWatchers? watchers = null)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        buffer.Added += OnAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        buffer.Added -= OnAdded;
        return Task.CompletedTask;
    }

    private void OnAdded(LogEntryView entry)
    {
        if (watchers is not null && watchers.Count("dashboard") == 0)
        {
            return;
        }

        _ = publisher.PublishDashboardEventAsync(
            SystemLogAndDatabasesEndpointRouteBuilderExtensions.LogEntryEventName,
            SystemLogAndDatabasesEndpointRouteBuilderExtensions.ToItem(entry),
            CancellationToken.None);
    }
}
