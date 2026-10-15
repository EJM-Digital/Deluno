using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The System > Health and System > Monitoring routes over HTTP, on the whole application (#1294): the list the Health card
/// reads, the snooze, the clear for a one-time note, the start-up step retry, and the three monitoring reads (the dashboard and
/// the two exports). Each answer is checked against what the screen does with it.
/// </summary>
public sealed class SystemHealthAndMonitoringRoutesTests
{
    private static async Task SeedAsync(ApplicationTestHost app, params HealthCheckOutcome[] outcomes)
        => await app.Services.GetRequiredService<IHealthCheckRepository>().UpsertResultsAsync(outcomes, CancellationToken.None);

    private static HealthCheckOutcome Problem(string id) => HealthCheckOutcome.Problem(id, HealthCheckCategories.DownloadClient, $"Name of {id}", $"{id} is down.", "It is off.", "Turn it on.");

    // ------------------------------------------------------------------ GET /api/system/health

    [Fact]
    public async Task The_health_list_returns_every_row_with_a_summary_that_leaves_out_snoozed_problems_and_counts_unknowns_and_suggestions()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(
            app,
            Problem("test:problem"),
            Problem("test:snoozed"),
            HealthCheckOutcome.Ok("test:fine", HealthCheckCategories.Library, "Fine", "All good."),
            HealthCheckOutcome.Unknown("test:unknown", HealthCheckCategories.Library, "Unknown", "Not answered yet."),
            HealthCheckOutcome.Suggestion("test:suggestion", HealthCheckCategories.Library, "Suggestion", "Worth turning on.", null, null));
        using var snooze = await app.Client.PostAsJsonAsync("/api/system/health/test:snoozed/snooze", new { hours = 2 });
        snooze.EnsureSuccessStatusCode();

        var body = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/health");

        var ids = body.GetProperty("checks").EnumerateArray().Select(row => row.GetProperty("id").GetString()).ToArray();
        foreach (var id in new[] { "test:problem", "test:snoozed", "test:fine", "test:unknown", "test:suggestion" }) Assert.Contains(id, ids);
        var problem = body.GetProperty("checks").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "test:problem");
        Assert.Equal("problem", problem.GetProperty("status").GetString());
        Assert.Equal("It is off.", problem.GetProperty("cause").GetString());
        Assert.Equal("Turn it on.", problem.GetProperty("fix").GetString());
        var summary = body.GetProperty("summary");
        Assert.True(summary.GetProperty("problemCount").GetInt32() >= 2, "both problems are problems");
        Assert.Equal(summary.GetProperty("problemCount").GetInt32() - 1, summary.GetProperty("unsnoozedProblemCount").GetInt32());
        Assert.True(summary.GetProperty("unknownCount").GetInt32() >= 1);
        Assert.True(summary.GetProperty("suggestionCount").GetInt32() >= 1);
    }

    // ------------------------------------------------------------------ POST /{id}/snooze

    [Fact]
    public async Task Snoozing_an_unknown_row_answers_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsJsonAsync("/api/system/health/test:nothing-here/snooze", new { hours = 24 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5000, 720)]
    [InlineData(720, 720)]
    [InlineData(48, 48)]
    [InlineData(0, 24)]
    [InlineData(-3, 24)]
    public async Task A_snooze_lasts_the_hours_asked_for_clamped_to_one_to_720_and_a_missing_or_zero_count_means_24(int asked, int expectedHours)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app, Problem("test:problem"));

        using var response = await app.Client.PostAsJsonAsync("/api/system/health/test:problem/snooze", new { hours = asked });

        response.EnsureSuccessStatusCode();
        Assert.Equal(expectedHours, await HoursSnoozedAsync(response));
    }

    [Fact]
    public async Task A_snooze_with_no_body_at_all_lasts_24_hours()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app, Problem("test:problem"));

        using var response = await app.Client.PostAsync("/api/system/health/test:problem/snooze", content: null);

        response.EnsureSuccessStatusCode();
        Assert.Equal(24, await HoursSnoozedAsync(response));
    }

    [Fact]
    public async Task Unsnoozing_clears_the_snooze_and_an_unknown_row_answers_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app, Problem("test:problem"));
        (await app.Client.PostAsJsonAsync("/api/system/health/test:problem/snooze", new { hours = 5 })).EnsureSuccessStatusCode();

        using var cleared = await app.Client.PostAsync("/api/system/health/test:problem/unsnooze", content: null);
        using var unknown = await app.Client.PostAsync("/api/system/health/test:nothing-here/unsnooze", content: null);

        cleared.EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("snoozedUntilUtc").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static async Task<int> HoursSnoozedAsync(HttpResponseMessage response)
    {
        var until = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("snoozedUntilUtc").GetDateTimeOffset();
        // The answer carries its own end; a minute either way absorbs the time the request took.
        return (int)Math.Round((until - DateTimeOffset.UtcNow).TotalHours);
    }

    // ------------------------------------------------------------------ DELETE /notices/{id}

    [Fact]
    public async Task Clearing_a_note_deletes_it_and_answers_204()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app, HealthCheckOutcome.Problem("notice:test-note", HealthCheckCategories.Notice, "A note", "Something happened once.", null, null));

        using var response = await app.Client.DeleteAsync("/api/system/health/notices/notice:test-note");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await app.Services.GetRequiredService<IHealthCheckRepository>().GetAsync("notice:test-note", CancellationToken.None));
    }

    [Fact]
    public async Task Clearing_an_id_that_is_not_a_note_answers_404_and_leaves_the_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app, Problem("indexer:1"));

        using var response = await app.Client.DeleteAsync("/api/system/health/notices/indexer:1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await app.Services.GetRequiredService<IHealthCheckRepository>().GetAsync("indexer:1", CancellationToken.None));
    }

    [Fact]
    public async Task Clearing_a_note_that_does_not_exist_answers_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.DeleteAsync("/api/system/health/notices/notice:never-raised");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------ background problems

    private sealed class FakeRetry(string key, bool succeeds) : IBackgroundProblemRetry
    {
        public int Runs { get; private set; }
        public string ProblemKey => key;
        public Task<bool> RetryAsync(CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(succeeds);
        }
    }

    [Fact]
    public async Task The_retry_runs_the_step_registered_under_that_key_and_says_whether_it_cleared()
    {
        var works = new FakeRetry("startup:works", succeeds: true);
        var fails = new FakeRetry("startup:fails", succeeds: false);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.AddSingleton<IBackgroundProblemRetry>(works);
            services.AddSingleton<IBackgroundProblemRetry>(fails);
        });

        var cleared = await (await app.Client.PostAsync("/api/system/health/background-problems/startup:works/retry", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        var notCleared = await (await app.Client.PostAsync("/api/system/health/background-problems/startup:fails/retry", content: null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(cleared.GetProperty("cleared").GetBoolean());
        Assert.Equal("It worked, so the note is gone.", cleared.GetProperty("message").GetString());
        Assert.False(notCleared.GetProperty("cleared").GetBoolean());
        Assert.Contains("It still did not work", notCleared.GetProperty("message").GetString());
        Assert.Equal(1, works.Runs);
        Assert.Equal(1, fails.Runs);
    }

    [Fact]
    public async Task The_retry_for_a_key_nothing_is_registered_under_answers_404_and_runs_nothing()
    {
        var works = new FakeRetry("startup:works", succeeds: true);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IBackgroundProblemRetry>(works));

        using var response = await app.Client.PostAsync("/api/system/health/background-problems/startup:unknown/retry", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, works.Runs);
    }

    [Fact]
    public async Task Clearing_a_background_problem_answers_204_for_a_start_up_key_and_404_for_any_other()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var startup = await app.Client.DeleteAsync("/api/system/health/background-problems/startup:collection-exclusions");
        using var lane = await app.Client.DeleteAsync("/api/system/health/background-problems/lane:import");

        Assert.Equal(HttpStatusCode.NoContent, startup.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, lane.StatusCode);
    }

    // ------------------------------------------------------------------ monitoring

    [Fact]
    public async Task The_dashboard_answers_readiness_services_storage_performance_and_alerts()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var body = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/dashboard");

        Assert.True(body.GetProperty("readiness").GetProperty("totalChecks").GetInt32() > 0);
        Assert.True(body.GetProperty("readiness").GetProperty("failedChecks").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("services").ValueKind);
        Assert.True(body.GetProperty("storage").TryGetProperty("freePercent", out _));
        Assert.True(body.GetProperty("performance").GetProperty("apiLatency").TryGetProperty("p95Ms", out _));
        Assert.Equal(JsonValueKind.Array, body.GetProperty("alerts").ValueKind);
        Assert.True(body.GetProperty("generatedUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task The_Prometheus_export_is_plain_text_with_a_help_and_type_header_and_its_metrics_sorted()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.GetAsync("/api/monitoring/export/prometheus");

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("version=0.0.4", response.Content.Headers.ContentType?.ToString());
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("# HELP deluno_monitoring_readiness_ready", lines[0]);
        Assert.Equal("# TYPE deluno_monitoring_readiness_ready gauge", lines[1]);
        var metrics = lines.Skip(2).ToArray();
        Assert.NotEmpty(metrics);
        var names = metrics.Select(line => line.Split(' ')[0]).ToArray();
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal).ToArray(), names);
        Assert.Contains("deluno_monitoring_readiness_ready", names);
        Assert.Contains("deluno_monitoring_alerts_open", names);
        Assert.All(metrics, line => Assert.True(double.TryParse(line.Split(' ')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _), $"'{line}' has no number"));
    }

    [Fact]
    public async Task The_Influx_export_is_one_line_of_name_value_fields_ending_in_a_nanosecond_timestamp()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var before = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds() * 1_000_000L;

        using var response = await app.Client.GetAsync("/api/monitoring/export/influx");

        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        var line = Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("deluno_monitoring,instance=local ", line);
        var parts = line.Split(' ');
        Assert.Equal(3, parts.Length);
        var fields = parts[1].Split(',');
        Assert.Contains(fields, field => field.StartsWith("deluno_monitoring_readiness_ready=", StringComparison.Ordinal));
        Assert.All(fields, field => Assert.True(double.TryParse(field.Split('=')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _), $"'{field}' is not name=value"));
        var timestamp = long.Parse(parts[2], CultureInfo.InvariantCulture);
        Assert.True(timestamp > before, "the timestamp is in nanoseconds since the Unix epoch");
        Assert.True(timestamp < DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds() * 1_000_000L);
    }
}
