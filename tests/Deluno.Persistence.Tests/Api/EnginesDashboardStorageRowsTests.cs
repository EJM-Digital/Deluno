using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.Monitoring;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The storage block of the monitoring dashboard on the real host and the real data folder: what it reads from the volume,
/// the one low-storage threshold, and the answer when the volume cannot be read. Refs #1296.
/// </summary>
public sealed class EnginesDashboardStorageRowsTests
{
    [Fact]
    public async Task The_dashboard_reports_the_data_folder_and_the_volume_it_is_on_with_the_free_percent_worked_out_from_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var storage = (await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/dashboard")).GetProperty("storage");

        Assert.Equal(Path.GetFullPath(app.DataRoot), storage.GetProperty("dataRoot").GetString());
        var total = storage.GetProperty("totalBytes").GetInt64();
        var free = storage.GetProperty("freeBytes").GetInt64();
        Assert.True(total > 0 && free > 0 && free <= total);
        Assert.Equal(Math.Round((double)free / total * 100d, 2), storage.GetProperty("freePercent").GetDouble(), 1);
        // A volume with more than 12% free (the default threshold) is not low.
        if (storage.GetProperty("freePercent").GetDouble() > 12d)
        {
            Assert.False(storage.GetProperty("lowStorage").GetBoolean());
        }
    }

    [Fact]
    public async Task One_threshold_decides_low_storage_so_a_volume_under_it_is_flagged_and_the_exported_metric_agrees()
    {
        // 95 is the highest the threshold can be set: any volume with 95% or less free is low.
        await using var app = await ApplicationTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Deluno:Monitoring:LowStorageThresholdPercent"] = "95" });

        var storage = (await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/dashboard")).GetProperty("storage");

        Assert.True(storage.GetProperty("freePercent").GetDouble() <= 95d);
        Assert.True(storage.GetProperty("lowStorage").GetBoolean());
        var metrics = await app.Client.GetStringAsync("/api/monitoring/export/prometheus");
        Assert.Contains("deluno_monitoring_storage_low 1", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_data_folder_that_cannot_be_read_is_reported_as_unknown_and_the_failure_is_logged_not_thrown()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var logger = new CapturingLogger();
        // A folder name no file system accepts: the volume reading throws before it can answer.
        var monitoring = ActivatorUtilities.CreateInstance<MonitoringService>(
            app.Services,
            Options.Create(new StoragePathOptions { DataRoot = "not\0a\0folder" }),
            logger);

        var storage = (await monitoring.GetDashboardAsync(CancellationToken.None)).Storage;

        Assert.Equal("not\0a\0folder", storage.DataRoot);
        Assert.Null(storage.TotalBytes);
        Assert.Null(storage.FreeBytes);
        Assert.Null(storage.FreePercent);
        Assert.False(storage.LowStorage);
        Assert.Contains(logger.Warnings, warning => warning.Contains("Could not read storage information", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : ILogger<MonitoringService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
