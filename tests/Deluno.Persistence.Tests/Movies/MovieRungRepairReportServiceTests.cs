using System.Reflection;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// #1065: the start-up Activity report about repaired movies can never stop
/// Deluno starting. A failure is shown on Health under one stable key (so a
/// repeat failure updates the same entry) and cleared once the report is
/// written; the repaired rows stay unreported until then, so it retries next start.
/// </summary>
public sealed class MovieRungRepairReportServiceTests
{
    [Fact]
    public async Task A_failing_report_does_not_stop_start_up_is_shown_on_Health_once_and_retried_next_start()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-30T00:00:00Z"));
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None))
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO movie_rung_repairs (movie_id, library_id, repaired_utc, reported) VALUES ('m1', 'l1', '2026-09-30T00:00:00Z', 0);";
            await insert.ExecuteNonQueryAsync();
        }

        var activity = ActivityProxy.Create(failFirst: 2);
        var reporter = new FakeReporter();
        var service = new MovieRungRepairReportService(
            storage.Factory, activity, NullLogger<MovieRungRepairReportService>.Instance, reporter);

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(2, reporter.Reported.Count);
        Assert.All(reporter.Reported, key => Assert.Equal(MovieRungRepairReportService.ProblemKey, key));
        Assert.Empty(reporter.Cleared);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal([MovieRungRepairReportService.ProblemKey], reporter.Cleared);
        await using var check = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        await using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM movie_rung_repairs WHERE reported = 0;";
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    private sealed class FakeReporter : IBackgroundProblemReporter
    {
        public List<string> Reported { get; } = [];
        public List<string> Cleared { get; } = [];
        public void Report(string key, string title, string message, string reason) => Reported.Add(key);
        public void Clear(string key) => Cleared.Add(key);
    }

    /// <summary>Throws from RecordActivityAsync for the first calls, then succeeds.</summary>
    public class ActivityProxy : DispatchProxy
    {
        private int _failuresLeft;

        public static IActivityFeedRepository Create(int failFirst)
        {
            var proxy = Create<IActivityFeedRepository, ActivityProxy>();
            ((ActivityProxy)(object)proxy)._failuresLeft = failFirst;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IActivityFeedRepository.RecordActivityAsync))
            {
                throw new NotSupportedException(targetMethod.Name);
            }

            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                throw new IOException("database is locked");
            }

            return Task.FromResult<ActivityEventItem>(null!);
        }
    }
}
