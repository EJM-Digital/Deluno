using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What a person can do with a finding on System, Health: snooze it for a bounded time, bring it back, clear a one-time note,
/// retry or clear a start-up step that did not finish; and the metrics Deluno publishes for Prometheus and InfluxDB. Real host,
/// real database file.
/// </summary>
public sealed class HealthFindingRoutesTests
{
    private static async Task AddProblemAsync(ApplicationTestHost app, string id)
        => await app.Services.GetRequiredService<IHealthCheckRepository>().UpsertResultsAsync(
            [HealthCheckOutcome.Problem(id, HealthCheckCategories.DownloadClient, "Example finding", "Something is wrong.", "A cause.", "A fix.")],
            CancellationToken.None);

    private static async Task<HealthCheckResultItem?> RowAsync(ApplicationTestHost app, string id)
        => await app.Services.GetRequiredService<IHealthCheckRepository>().GetAsync(id, CancellationToken.None);

    [Fact]
    public async Task A_finding_is_snoozed_for_a_bounded_time_and_brought_back_and_an_unknown_finding_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await AddProblemAsync(app, "example:one");

        var twoHours = await app.Client.PostAsJsonAsync("/api/system/health/example:one/snooze", new { hours = 2 });
        Assert.Equal(HttpStatusCode.OK, twoHours.StatusCode);
        Assert.InRange(((await RowAsync(app, "example:one"))!.SnoozedUntilUtc!.Value - DateTimeOffset.UtcNow).TotalHours, 1.9, 2.1);

        await app.Client.PostAsJsonAsync("/api/system/health/example:one/snooze", new { hours = 100000 });
        Assert.InRange(((await RowAsync(app, "example:one"))!.SnoozedUntilUtc!.Value - DateTimeOffset.UtcNow).TotalHours, 719, 721);

        await app.Client.PostAsync("/api/system/health/example:one/snooze", content: null);
        Assert.InRange(((await RowAsync(app, "example:one"))!.SnoozedUntilUtc!.Value - DateTimeOffset.UtcNow).TotalHours, 23, 25);
        var summary = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/health/summary");
        Assert.Equal(1, summary.GetProperty("problemCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("unsnoozedProblemCount").GetInt32());

        var back = await app.Client.PostAsync("/api/system/health/example:one/unsnooze", content: null);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Null((await RowAsync(app, "example:one"))!.SnoozedUntilUtc);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/system/health/no-such-finding/snooze", new { hours = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/system/health/no-such-finding/unsnooze", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PostAsync("/api/system/health/example:one/snooze", content: null)).StatusCode);
    }

    [Fact]
    public async Task A_one_time_note_is_cleared_by_hand_but_an_ordinary_finding_is_never_removed_this_way()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await AddProblemAsync(app, "notice:moved-away");
        await AddProblemAsync(app, "example:real-problem");

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/system/health/notices/example:real-problem")).StatusCode);
        Assert.NotNull(await RowAsync(app, "example:real-problem"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/system/health/notices/notice:never-existed")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync("/api/system/health/notices/notice:moved-away")).StatusCode);
        Assert.Null(await RowAsync(app, "notice:moved-away"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/system/health/notices/notice:moved-away")).StatusCode);
    }

    [Fact]
    public async Task A_start_up_step_that_did_not_finish_can_be_tried_again_or_cleared_and_nothing_else_can_be_cleared_this_way()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var reporter = app.Services.GetRequiredService<IBackgroundProblemReporter>();
        reporter.Report("startup:collection-exclusions", "Collection exclusions were not moved", "It did not finish.", "database is locked");

        var retried = await app.Client.PostAsync("/api/system/health/background-problems/startup:collection-exclusions/retry", content: null);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var answer = await retried.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(answer.GetProperty("cleared").GetBoolean());
        Assert.Equal("It worked, so the note is gone.", answer.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/system/health/background-problems/startup:nothing-like-it/retry", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync("/api/system/health/background-problems/startup:collection-exclusions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/system/health/background-problems/lane:import")).StatusCode);
    }

    [Fact]
    public async Task The_metrics_are_published_in_the_prometheus_text_format_and_as_an_influx_line_with_the_same_numbers()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var prometheus = await app.Client.GetAsync("/api/monitoring/export/prometheus");
        var influx = await app.Client.GetAsync("/api/monitoring/export/influx");

        Assert.Equal(HttpStatusCode.OK, prometheus.StatusCode);
        Assert.Equal("text/plain", prometheus.Content.Headers.ContentType!.MediaType);
        var text = await prometheus.Content.ReadAsStringAsync();
        Assert.StartsWith("# HELP deluno_monitoring_readiness_ready", text, StringComparison.Ordinal);
        Assert.Contains("# TYPE deluno_monitoring_readiness_ready gauge", text, StringComparison.Ordinal);
        var samples = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#')).ToArray();
        Assert.NotEmpty(samples);
        Assert.All(samples, line => Assert.Matches(@"^[a-z_:][a-z0-9_:]* -?\d+(\.\d+)?$", line));

        Assert.Equal(HttpStatusCode.OK, influx.StatusCode);
        var line0 = (await influx.Content.ReadAsStringAsync()).TrimEnd('\n');
        Assert.StartsWith("deluno_monitoring,instance=local ", line0, StringComparison.Ordinal);
        Assert.Matches(@" \d{19}$", line0);
        foreach (var sample in samples)
        {
            var parts = sample.Split(' ');
            Assert.Contains($"{parts[0]}={parts[1]}", line0, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().GetAsync("/api/monitoring/export/prometheus")).StatusCode);
    }
}
