using System.Data.Common;
using System.Diagnostics;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1081: no screen's API call takes longer than 150 ms on a library the size of
/// a real one.
///
/// <para>The database is seeded to a size a long-running install reaches: ten
/// thousand jobs, five thousand downloads, twenty thousand activity rows, three
/// thousand films and three hundred shows. Each endpoint a screen opens on load
/// is then called; the best of three calls after a warm-up is what is timed, so a
/// single scheduling hiccup on a shared runner does not fail it while a scan that
/// costs every time still does. The numbers are printed so a change can be read
/// against them.</para>
/// </summary>
public sealed class ScreenEndpointsStayFastTests(ITestOutputHelper output, ScreenEndpointsStayFastTests.SeededHost seeded)
    : IClassFixture<ScreenEndpointsStayFastTests.SeededHost>
{
    private const double BudgetMilliseconds = 150;

    public static TheoryData<string> Endpoints => new()
    {
        "/api/jobs?pageSize=80",
        "/api/jobs?pageSize=24&includePayload=true",
        "/api/jobs?failed=true&pageSize=500",
        "/api/download-dispatches?pageSize=50",
        "/api/download-dispatches?pageSize=50&includeArchived=true",
        "/api/activity?pageSize=50",
        "/api/activity?pageSize=50&relatedEntityType=movie",
        "/api/movies/page?pageSize=50",
        "/api/movies/page?pageSize=50&facets=false",
        "/api/movies/page?pageSize=50&status=missing",
        "/api/series/page?pageSize=50",
        "/api/series/page?pageSize=50&facets=false",
        "/api/dashboard/metrics?days=30",
        "/api/monitoring/dashboard",
        "/api/indexers/scoreboard?days=30",
        "/api/libraries",
        "/api/movies/import-recovery",
        "/api/series/import-recovery"
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_screen_call_answers_inside_the_budget_on_a_seeded_library(string path)
    {
        var app = await seeded.GetAsync();

        // Warm-up: connection pool, JIT, the first page of the plan cache. Its time is
        // printed too: an endpoint that keeps its answer shows its real cost here.
        var coldClock = Stopwatch.StartNew();
        var cold = await app.Client.GetAsync(path);
        await cold.Content.ReadAsByteArrayAsync();
        coldClock.Stop();
        cold.EnsureSuccessStatusCode();

        var best = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var clock = Stopwatch.StartNew();
            var response = await app.Client.GetAsync(path);
            await response.Content.ReadAsByteArrayAsync();
            clock.Stop();
            response.EnsureSuccessStatusCode();
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }

        output.WriteLine($"{path,-60} first {coldClock.Elapsed.TotalMilliseconds,7:F1} ms   then {best,7:F1} ms");
        Assert.True(best < BudgetMilliseconds, $"{path} took {best:F1} ms (budget {BudgetMilliseconds} ms).");
    }

    /// <summary>One seeded host for every path: seeding is the slow part, reading is what is timed.</summary>
    public sealed class SeededHost : IAsyncLifetime
    {
        private ApplicationTestHost? _app;

        internal async Task<ApplicationTestHost> GetAsync()
        {
            if (_app is null)
            {
                _app = await ApplicationTestHost.StartAsync();
                await SeedAsync(_app);
            }

            return _app;
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_app is not null) await _app.DisposeAsync();
        }
    }

    private static async Task SeedAsync(ApplicationTestHost app)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        string Stamp(int index) => start.AddSeconds(index * 30).ToString("O");

        await using (var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None))
        await using (var transaction = await connection.BeginTransactionAsync(CancellationToken.None))
        {
            var batch = new Batch(connection, transaction);
            // A torrent file carried in a payload is what makes a job row heavy.
            var payload = "{\"torrentFile\":\"" + new string('A', 4000) + "\"}";
            var statuses = new[] { "completed", "completed", "completed", "completed", "failed", "queued", "dead-letter" };
            for (var i = 0; i < 10_000; i++)
            {
                var status = statuses[i % statuses.Length];
                await batch.ExecAsync(
                    "INSERT INTO job_queue (id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc, completed_utc, last_error, related_entity_type, related_entity_id, max_attempts) " +
                    "VALUES ($id, 'media.probe', 'test', $status, $payload, 1, $created, $created, $completed, $error, 'movie', $entity, 3);",
                    ("$id", $"job-{i:D6}"), ("$status", status), ("$payload", payload), ("$created", Stamp(i)),
                    ("$completed", status is "queued" ? null : Stamp(i)), ("$error", status is "failed" or "dead-letter" ? "boom" : null),
                    ("$entity", $"movie-{i % 3000:D6}"));
            }

            // The newest hundred grabs carry a whole search plan, as they did on the rig:
            // hundreds of kilobytes, stored as the notes and again as the grab response.
            var heavy = "{\"searchPlan\":{\"candidates\":[" + string.Join(",", Enumerable.Range(0, 400).Select(n => "{\"releaseName\":\"Some.Release." + n + "\",\"reasons\":\"" + new string('r', 700) + "\"}")) + "]}}";
            for (var i = 0; i < 5_000; i++)
            {
                var notes = i >= 4_900 ? heavy : "{\"message\":\"ok\"}";
                await batch.ExecAsync(
                    "INSERT INTO download_dispatches (id, library_id, media_type, entity_type, entity_id, release_name, indexer_name, download_client_id, download_client_name, status, notes_json, grab_response_json, created_utc, grab_attempted_utc, grab_status, indexer_id) " +
                    "VALUES ($id, 'library-films', 'movie', 'movie', $entity, $name, 'idx', 'client', 'Client', $status, $notes, $notes, $created, $attempted, 'succeeded', 'indexer-1');",
                    ("$id", $"dispatch-{i:D6}"), ("$entity", $"movie-{i % 3000:D6}"), ("$name", $"Release {i}"), ("$notes", notes),
                    ("$status", i % 10 == 0 ? "archived" : "initial"), ("$created", Stamp(i)), ("$attempted", i % 2 == 0 ? Stamp(i + 1) : null));
            }

            for (var i = 0; i < 20_000; i++)
            {
                await batch.ExecAsync(
                    "INSERT INTO activity_events (id, category, message, details_json, related_entity_type, related_entity_id, created_utc) " +
                    "VALUES ($id, 'search.run', 'ran', $details, $type, $entity, $created);",
                    ("$id", $"activity-{i:D6}"), ("$details", i >= 19_900 ? heavy : null), ("$type", i % 3 == 0 ? "movie" : i % 3 == 1 ? "series" : null),
                    ("$entity", $"movie-{i % 3000:D6}"), ("$created", Stamp(i)));
            }

            for (var i = 0; i < 20_000; i++)
            {
                await batch.ExecAsync(
                    "INSERT INTO indexer_query_events (id, indexer_id, indexer_name, query_text, categories, media_type, query_kind, outcome, elapsed_ms, candidate_count, created_utc) " +
                    "VALUES ($id, $indexer, $indexer, 'q', '2000', 'movie', 'search', 'success', 120, 5, $created);",
                    ("$id", $"query-{i:D6}"), ("$indexer", $"indexer-{i % 6}"), ("$created", DateTimeOffset.UtcNow.AddMinutes(-i / 5).ToString("O")));
            }

            await transaction.CommitAsync(CancellationToken.None);
        }

        await using (var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None))
        await using (var transaction = await connection.BeginTransactionAsync(CancellationToken.None))
        {
            var batch = new Batch(connection, transaction);
            const string now = "2026-08-27T00:00:00.0000000+00:00";
            for (var i = 0; i < 3_000; i++)
            {
                var id = $"movie-{i:D6}";
                await batch.ExecAsync(
                    "INSERT INTO movie_entries (id, title, release_year, monitored, created_utc, updated_utc) VALUES ($id, $title, $year, 1, $now, $now);",
                    ("$id", id), ("$title", $"Title {i:D6}"), ("$year", 1990 + i % 30), ("$now", now));
                var rung = i % 4;
                await batch.ExecAsync(
                    "INSERT INTO movie_wanted_state (movie_id, library_id, wanted_status, wanted_reason, has_file, current_quality, target_quality, quality_cutoff_met, updated_utc) " +
                    "VALUES ($id, 'library-films', $status, 'seeded', $hasFile, $quality, 'WEB 2160p', $cutoff, $now);",
                    ("$id", id), ("$status", rung switch { 0 => "missing", 1 => "upgrade", 2 => "covered", _ => "upcoming" }),
                    ("$hasFile", rung is 1 or 2 ? 1 : 0), ("$quality", rung is 1 or 2 ? "WEB 1080p" : null), ("$cutoff", rung == 2 ? 1 : 0), ("$now", now));
            }

            for (var i = 0; i < 500; i++)
            {
                await batch.ExecAsync(
                    "INSERT INTO movie_import_recovery_cases (id, title, failure_kind, summary, recommended_action, details_json, detected_utc, status) " +
                    "VALUES ($id, $title, 'unmatched', 'No match', 'Pick a title', $details, $detected, 'open');",
                    ("$id", $"case-{i:D4}"), ("$title", $"Case {i}"), ("$details", "{\"files\":\"" + new string('x', 2000) + "\"}"), ("$detected", Stamp(i)));
            }

            await transaction.CommitAsync(CancellationToken.None);
        }

        await using (var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None))
        await using (var transaction = await connection.BeginTransactionAsync(CancellationToken.None))
        {
            var batch = new Batch(connection, transaction);
            const string now = "2026-08-27T00:00:00.0000000+00:00";
            for (var i = 0; i < 300; i++)
            {
                var id = $"series-{i:D5}";
                await batch.ExecAsync(
                    "INSERT INTO series_entries (id, title, start_year, monitored, created_utc, updated_utc) VALUES ($id, $title, $year, 1, $now, $now);",
                    ("$id", id), ("$title", $"Show {i:D5}"), ("$year", 1990 + i % 30), ("$now", now));
                await batch.ExecAsync(
                    "INSERT INTO series_wanted_state (series_id, library_id, wanted_status, wanted_reason, has_file, updated_utc) VALUES ($id, 'library-tv', $status, 'seeded', $hasFile, $now);",
                    ("$id", id), ("$status", i % 2 == 0 ? "missing" : "covered"), ("$hasFile", i % 2), ("$now", now));
                for (var episode = 1; episode <= 10; episode++)
                {
                    var episodeId = $"{id}-e{episode}";
                    await batch.ExecAsync(
                        "INSERT INTO episode_entries (id, series_id, season_number, episode_number, monitored, has_file, created_utc, updated_utc) VALUES ($id, $series, 1, $number, 1, $hasFile, $now, $now);",
                        ("$id", episodeId), ("$series", id), ("$number", episode), ("$hasFile", i % 2), ("$now", now));
                    await batch.ExecAsync(
                        "INSERT INTO episode_wanted_state (episode_id, series_id, library_id, wanted_status, wanted_reason, updated_utc) VALUES ($id, $series, 'library-tv', $status, 'seeded', $now);",
                        ("$id", episodeId), ("$series", id), ("$status", i % 2 == 0 ? "missing" : "covered"), ("$now", now));
                }
            }

            await transaction.CommitAsync(CancellationToken.None);
        }
    }

    /// <summary>Prepares each statement once and rebinds it per row; a fresh command per row is what made seeding slow.</summary>
    private sealed class Batch(DbConnection connection, DbTransaction transaction)
    {
        private readonly Dictionary<string, DbCommand> _commands = new();

        public async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            if (!_commands.TryGetValue(sql, out var command))
            {
                command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                foreach (var (name, _) in parameters)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    command.Parameters.Add(parameter);
                }

                _commands[sql] = command;
            }

            foreach (var (name, value) in parameters)
            {
                command.Parameters[name].Value = value ?? DBNull.Value;
            }

            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
