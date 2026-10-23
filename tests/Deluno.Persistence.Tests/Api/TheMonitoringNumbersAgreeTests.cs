using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The monitoring numbers say the same thing everywhere they are shown (coverage register, engines): the dashboard's low-storage flag,
/// the storage.low alert and the exported deluno_monitoring_storage_low metric all use the one threshold the person configured, and
/// both exports carry every metric the dashboard has. Refs #1296.
/// </summary>
public sealed class TheMonitoringNumbersAgreeTests
{
    private static readonly IReadOnlyDictionary<string, string?> EveryDriveIsLow = new Dictionary<string, string?>
    {
        // The widest threshold there is: a drive with 95 percent or less free is low, which every real drive is.
        ["Deluno:Monitoring:LowStorageThresholdPercent"] = "95"
    };

    private static async Task<Dictionary<string, double>> PrometheusAsync(ApplicationTestHost app)
    {
        var text = await app.Client.GetStringAsync("/api/monitoring/export/prometheus");
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .ToDictionary(line => line.Split(' ')[0], line => double.Parse(line.Split(' ')[1], NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_threshold_the_person_set_is_the_one_the_dashboard_the_alert_and_the_exported_metric_all_use()
    {
        await using var app = await ApplicationTestHost.StartAsync(settings: EveryDriveIsLow);

        var dashboard = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/dashboard");
        var metrics = await PrometheusAsync(app);

        Assert.True(dashboard.GetProperty("storage").GetProperty("freePercent").GetDouble() <= 95d);
        var alert = Assert.Single(dashboard.GetProperty("alerts").EnumerateArray(), item => item.GetProperty("code").GetString() == "storage.low");
        Assert.Equal("critical", alert.GetProperty("severity").GetString());
        Assert.Contains("% free remains under " + dashboard.GetProperty("storage").GetProperty("dataRoot").GetString(), alert.GetProperty("details").GetString());
        Assert.True(dashboard.GetProperty("storage").GetProperty("lowStorage").GetBoolean(), "the alert says storage is low, so the flag must too");
        Assert.Equal(1d, metrics["deluno_monitoring_storage_low"]);
    }

    [Fact]
    public async Task With_the_default_threshold_a_drive_with_plenty_free_is_not_low_anywhere()
    {
        await using var app = await ApplicationTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Deluno:Monitoring:LowStorageThresholdPercent"] = "1" });

        var dashboard = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/dashboard");
        var metrics = await PrometheusAsync(app);

        Assert.DoesNotContain(dashboard.GetProperty("alerts").EnumerateArray(), alert => alert.GetProperty("code").GetString() == "storage.low");
        Assert.False(dashboard.GetProperty("storage").GetProperty("lowStorage").GetBoolean());
        Assert.Equal(0d, metrics["deluno_monitoring_storage_low"]);
    }

    [Fact]
    public async Task Both_exports_carry_every_metric_the_dashboard_has_with_a_decimal_point_and_the_counts_the_dashboard_shows()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var dashboard = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/dashboard");
        var metrics = await PrometheusAsync(app);
        var influx = await app.Client.GetStringAsync("/api/monitoring/export/influx");

        string[] always =
        [
            "deluno_monitoring_readiness_ready", "deluno_monitoring_readiness_failed_checks", "deluno_monitoring_storage_low",
            "deluno_monitoring_alerts_open", "deluno_monitoring_indexers_healthy", "deluno_monitoring_indexers_total",
            "deluno_monitoring_clients_healthy", "deluno_monitoring_clients_total", "deluno_monitoring_jobs_active",
            "deluno_monitoring_jobs_queued", "deluno_monitoring_jobs_failed", "deluno_monitoring_dispatch_alerts_open",
            "deluno_monitoring_api_requests", "deluno_monitoring_api_errors", "deluno_monitoring_api_error_rate_percent",
            "deluno_monitoring_api_latency_avg_ms", "deluno_monitoring_api_latency_p95_ms", "deluno_monitoring_search_cycles_sampled"
        ];
        Assert.All(always, name =>
        {
            Assert.True(metrics.ContainsKey(name), $"{name} is not in the Prometheus export");
            Assert.Contains($"{name}=", influx, StringComparison.Ordinal);
        });
        var services = dashboard.GetProperty("services");
        Assert.Equal(services.GetProperty("indexersTotal").GetInt32(), metrics["deluno_monitoring_indexers_total"]);
        Assert.Equal(services.GetProperty("indexersHealthy").GetInt32(), metrics["deluno_monitoring_indexers_healthy"]);
        Assert.Equal(services.GetProperty("downloadClientsTotal").GetInt32(), metrics["deluno_monitoring_clients_total"]);
        Assert.Equal(services.GetProperty("downloadClientsHealthy").GetInt32(), metrics["deluno_monitoring_clients_healthy"]);
        Assert.Equal(services.GetProperty("failedJobs").GetInt32(), metrics["deluno_monitoring_jobs_failed"]);
        Assert.Equal(dashboard.GetProperty("readiness").GetProperty("failedChecks").GetInt32(), metrics["deluno_monitoring_readiness_failed_checks"]);
        Assert.Equal(dashboard.GetProperty("alerts").GetArrayLength(), metrics["deluno_monitoring_alerts_open"]);
        // A decimal point, never a comma, whatever the machine's language is: every value parsed with the invariant culture above.
        Assert.DoesNotContain(',', string.Concat((await app.Client.GetStringAsync("/api/monitoring/export/prometheus")).Where(character => character != '\n')));
    }
}
