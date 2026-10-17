using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What raises each alert on Monitoring (GET /api/monitoring/alerts), with its severity and wording: the dispatch failure rate over
/// the last 24 hours (with a sample floor), open download problems, and failed system health checks. Real databases, the real route.
/// </summary>
public sealed class MonitoringAlertRulesTests
{
    private static async Task AddDispatchesAsync(ApplicationTestHost app, int total, int failed, string status = "dispatched", DateTimeOffset? attempted = null, string? idPrefix = null)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        for (var i = 0; i < total; i++)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO download_dispatches (id, library_id, media_type, entity_type, entity_id, release_name, indexer_name, download_client_id, download_client_name, status, created_utc, grab_status, grab_attempted_utc)
                VALUES (@id, 'lib', 'movies', 'movie', @entity, @release, 'Indexer', 'client', 'Client', @status, @at, @grab, @at);
                """;
            AddParameter(command, "@id", idPrefix is null ? Guid.NewGuid().ToString("N") : idPrefix + i);
            AddParameter(command, "@entity", "movie-" + i);
            AddParameter(command, "@release", "Release." + i);
            AddParameter(command, "@status", status);
            AddParameter(command, "@at", (attempted ?? DateTimeOffset.UtcNow).ToString("O"));
            AddParameter(command, "@grab", i < failed ? "failed" : "succeeded");
            await command.ExecuteNonQueryAsync();
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task<JsonElement[]> AlertsAsync(ApplicationTestHost app)
    {
        var body = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/alerts");
        return body.GetProperty("alerts").EnumerateArray().ToArray();
    }

    private static JsonElement? Alert(JsonElement[] alerts, string code)
        => alerts.Where(alert => alert.GetProperty("code").GetString() == code).Select(alert => (JsonElement?)alert).FirstOrDefault();

    [Fact]
    public async Task A_quarter_of_the_last_days_grabs_failing_raises_the_failure_rate_warning_with_the_counts()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await AddDispatchesAsync(app, total: 20, failed: 5);

        var alert = Alert(await AlertsAsync(app), "dispatch.failure-rate");

        Assert.NotNull(alert);
        Assert.Equal("warning", alert!.Value.GetProperty("severity").GetString());
        Assert.Equal("Dispatch failure rate exceeded threshold.", alert.Value.GetProperty("summary").GetString());
        Assert.Equal("Last 24h: 5/20 failed (25%).", alert.Value.GetProperty("details").GetString());
    }

    [Fact]
    public async Task Fewer_failures_than_the_threshold_or_too_few_grabs_to_judge_raise_no_failure_rate_warning()
    {
        await using var below = await ApplicationTestHost.StartAsync();
        await AddDispatchesAsync(below, total: 20, failed: 4);
        await using var tooFew = await ApplicationTestHost.StartAsync();
        await AddDispatchesAsync(tooFew, total: 19, failed: 19);

        Assert.Null(Alert(await AlertsAsync(below), "dispatch.failure-rate"));
        Assert.Null(Alert(await AlertsAsync(tooFew), "dispatch.failure-rate"));
    }

    [Fact]
    public async Task Failures_older_than_a_day_and_archived_grabs_are_not_counted_in_the_failure_rate()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await AddDispatchesAsync(app, total: 20, failed: 20, attempted: DateTimeOffset.UtcNow.AddHours(-30));
        await AddDispatchesAsync(app, total: 20, failed: 20, status: "archived");

        Assert.Null(Alert(await AlertsAsync(app), "dispatch.failure-rate"));
    }

    [Fact]
    public async Task An_open_download_problem_raises_the_recovery_warning_naming_how_many_and_settling_it_clears_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await AddDispatchesAsync(app, total: 2, failed: 0, idPrefix: "dispatch-");
        var problems = app.Services.GetRequiredService<IDispatchProblemLog>();
        await problems.RecordAsync("dispatch-0", "grab-failed", CancellationToken.None);
        await problems.RecordAsync("dispatch-1", "grab-failed", CancellationToken.None);

        var alert = Alert(await AlertsAsync(app), "dispatch.recovery-open");

        Assert.NotNull(alert);
        Assert.Equal("warning", alert!.Value.GetProperty("severity").GetString());
        Assert.Equal("Dispatch recovery alerts need attention.", alert.Value.GetProperty("summary").GetString());
        Assert.Equal("2 download problem(s) are open.", alert.Value.GetProperty("details").GetString());

        await problems.SettleAsync("dispatch-0", "grab-failed", CancellationToken.None);
        await problems.SettleAsync("dispatch-1", "grab-failed", CancellationToken.None);
        var metrics = await app.Services.GetRequiredService<IDispatchMetricsRepository>().GetMetricsAsync(CancellationToken.None);
        Assert.Equal(0, metrics.RecoveryCasesOpenCount);
    }

    [Fact]
    public async Task A_failed_readiness_check_raises_a_critical_alert_that_names_each_failed_check_and_sorts_first()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await AddDispatchesAsync(app, total: 20, failed: 10);
        using var readyResponse = await app.Client.GetAsync("/api/health/ready");
        var readiness = await readyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var failed = readiness.GetProperty("checks").EnumerateArray().Where(check => check.GetProperty("status").GetString() != "ready").ToArray();
        Assert.NotEmpty(failed); // the test host has no worker heartbeat yet, so readiness is not met

        var alerts = await AlertsAsync(app);

        var alert = Alert(alerts, "services.unhealthy");
        Assert.NotNull(alert);
        Assert.Equal("critical", alert!.Value.GetProperty("severity").GetString());
        Assert.Equal("One or more system health checks failed.", alert.Value.GetProperty("summary").GetString());
        foreach (var check in failed)
        {
            Assert.Contains($"{check.GetProperty("name").GetString()}: {check.GetProperty("message").GetString()}", alert.Value.GetProperty("details").GetString());
        }

        Assert.Equal("services.unhealthy", alerts[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_critical_alert_sorts_before_a_warning()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await AddDispatchesAsync(app, total: 20, failed: 10, idPrefix: "dispatch-");
        await app.Services.GetRequiredService<IDispatchProblemLog>().RecordAsync("dispatch-0", "grab-failed", CancellationToken.None);
        var alerts = await AlertsAsync(app);

        var severities = alerts.Select(alert => alert.GetProperty("severity").GetString()).ToArray();

        Assert.Contains("warning", severities);
        var order = severities.Select(severity => severity == "critical" ? 3 : severity == "warning" ? 2 : 1).ToArray();
        Assert.Equal(order.OrderByDescending(value => value), order);
    }
}
