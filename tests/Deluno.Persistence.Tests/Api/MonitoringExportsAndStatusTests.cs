using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The monitoring routes an outside tool reads (dashboard, alerts, diagnostics, the Prometheus and Influx exports) and the
/// health line of <c>/api/system/status</c>, on the real host. A fresh install has not yet recorded a worker heartbeat, so
/// it reads "not ready" with one open alert, which is what these tests lean on.
/// </summary>
public sealed class MonitoringExportsAndStatusTests
{
    [Fact]
    public async Task The_alerts_route_lists_the_dashboards_open_alerts_and_counts_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var dashboard = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/dashboard");
        var alerts = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/alerts");

        var fromDashboard = dashboard.GetProperty("alerts").EnumerateArray().Select(alert => alert.GetProperty("code").GetString()).ToArray();
        var listed = alerts.GetProperty("alerts").EnumerateArray().Select(alert => alert.GetProperty("code").GetString()).ToArray();
        Assert.Equal(fromDashboard, listed);
        Assert.Equal(listed.Length, alerts.GetProperty("openCount").GetInt32());
        Assert.NotEmpty(listed);
        Assert.False(dashboard.GetProperty("readiness").GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task The_status_health_line_reads_attention_while_a_check_fails_and_its_counts_are_the_dashboards()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/status");
        var dashboard = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/dashboard");

        var health = status.GetProperty("health");
        Assert.Equal("attention", health.GetProperty("status").GetString());
        Assert.Equal(dashboard.GetProperty("readiness").GetProperty("failedChecks").GetInt32(), health.GetProperty("failedChecks").GetInt32());
        Assert.Equal(dashboard.GetProperty("alerts").GetArrayLength(), health.GetProperty("openAlerts").GetInt32());
    }

    [Fact]
    public async Task The_diagnostics_route_pages_with_the_default_size_and_narrows_by_a_query_and_answers_an_empty_page_for_a_query_nothing_matches()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var all = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/diagnostics");
        var none = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/diagnostics?query=no-such-diagnostic-text&pageSize=5");

        Assert.Equal(JsonValueKind.Array, all.GetProperty("items").ValueKind);
        Assert.False(all.GetProperty("hasMore").GetBoolean());
        Assert.Empty(none.GetProperty("items").EnumerateArray());
        Assert.False(none.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("nextPageToken").ValueKind);
    }

    [Fact]
    public async Task The_prometheus_export_is_plain_text_with_one_sample_per_metric_and_the_influx_line_carries_the_same_metrics_and_a_nanosecond_time()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var prometheus = await app.Client.GetAsync("/api/monitoring/export/prometheus");
        var influx = await app.Client.GetAsync("/api/monitoring/export/influx");

        Assert.Equal(HttpStatusCode.OK, prometheus.StatusCode);
        Assert.StartsWith("text/plain", prometheus.Content.Headers.ContentType!.ToString());
        var promLines = (await prometheus.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("# TYPE deluno_monitoring_readiness_ready gauge", promLines);
        var samples = promLines.Where(line => !line.StartsWith('#')).ToArray();
        Assert.All(samples, line => Assert.Matches(@"^deluno_monitoring_[a-z0-9_]+ -?\d+(\.\d+)?$", line));
        var promNames = samples.Select(line => line.Split(' ')[0]).ToArray();
        Assert.Equal(promNames.Length, promNames.Distinct().Count());
        Assert.Equal(promNames.OrderBy(name => name, StringComparer.Ordinal), promNames);
        Assert.Contains("deluno_monitoring_readiness_ready 0", samples);

        var line = (await influx.Content.ReadAsStringAsync()).TrimEnd('\n');
        Assert.DoesNotContain('\n', line);
        var match = Regex.Match(line, @"^deluno_monitoring,instance=local (?<fields>\S+) (?<ns>\d{19})$");
        Assert.True(match.Success, line);
        var influxNames = match.Groups["fields"].Value.Split(',').Select(field => field.Split('=')[0]).ToArray();
        Assert.Equal(promNames, influxNames);
    }

    [Theory]
    [InlineData("/api/monitoring/dashboard")]
    [InlineData("/api/monitoring/alerts")]
    [InlineData("/api/monitoring/diagnostics")]
    [InlineData("/api/monitoring/export/prometheus")]
    [InlineData("/api/monitoring/export/influx")]
    public async Task Every_monitoring_read_needs_a_sign_in_or_a_key(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().GetAsync(route)).StatusCode);
    }
}
