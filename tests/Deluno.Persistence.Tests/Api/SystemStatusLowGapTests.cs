using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>Low-severity rows (#1329) of GET /api/system/status: the fields that say where and how Deluno runs, and the blocks that repeat other screens.</summary>
public sealed class SystemStatusLowGapTests
{
    [Fact]
    public async Task The_status_names_this_machine_and_how_Deluno_runs_on_it_in_the_stated_fields()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var environment = app.Services.GetRequiredService<IHostEnvironment>();

        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/status");

        Assert.Equal(DelunoHostName.Current, status.GetProperty("instanceName").GetString());
        Assert.Equal(JsonValueKind.String, status.GetProperty("buildTime").ValueKind);
        Assert.True(status.GetProperty("buildTime").GetDateTimeOffset() > DateTimeOffset.Parse("2020-01-01T00:00:00Z"));
        Assert.Equal(environment.IsDevelopment(), status.GetProperty("isDebug").GetBoolean());
        Assert.Equal(environment.IsProduction(), status.GetProperty("isProduction").GetBoolean());
        Assert.Equal(Environment.UserInteractive, status.GetProperty("isUserInteractive").GetBoolean());
        Assert.Equal(AppContext.BaseDirectory, status.GetProperty("startupPath").GetString());
        Assert.Equal(
            OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : RuntimeInformation.OSDescription,
            status.GetProperty("osName").GetString());
        Assert.Equal(Environment.OSVersion.Version.ToString(), status.GetProperty("osVersion").GetString());
        Assert.Equal(OperatingSystem.IsLinux(), status.GetProperty("isLinux").GetBoolean());
        Assert.Equal(OperatingSystem.IsMacOS(), status.GetProperty("isOsx").GetBoolean());
        Assert.Equal(
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) || File.Exists("/.dockerenv"),
            status.GetProperty("isDocker").GetBoolean());
        Assert.Equal("forms", status.GetProperty("authentication").GetString());
        Assert.Equal(string.Empty, status.GetProperty("urlBase").GetString());
        Assert.Equal(Environment.Version.ToString(), status.GetProperty("runtimeVersion").GetString());
        Assert.Equal(".NET", status.GetProperty("runtimeName").GetString());
    }

    [Fact]
    public async Task The_updates_block_and_the_branch_equal_the_update_status_and_the_tasks_block_counts_all_running_and_failing_system_tasks()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var sql = new[]
        {
            ("health.one", "running"), ("health.two", "running"), ("backup.one", "failed"), ("library.one", "completed"), ("library.two", "completed")
        };
        foreach (var (key, result) in sql)
        {
            await ApiScenes.ExecuteAsync(app, "jobs",
                $"INSERT INTO worker_schedule_state (schedule_key, last_run_utc, last_completed_utc, last_result, last_duration_ms, next_run_utc, interval_seconds, last_error) VALUES ('{key}', '2026-10-29T00:00:00Z', NULL, '{result}', 10, NULL, 60, NULL);");
        }

        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/status");
        var updates = await app.Client.GetFromJsonAsync<JsonElement>("/api/updates/status");

        var block = status.GetProperty("updates");
        Assert.Equal(updates.GetProperty("channel").GetString(), block.GetProperty("channel").GetString());
        Assert.Equal(updates.GetProperty("updateAvailable").GetBoolean(), block.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal(updates.GetProperty("latestVersion").ValueKind, block.GetProperty("latestVersion").ValueKind);
        Assert.Equal(updates.GetProperty("state").GetString(), block.GetProperty("state").GetString());
        Assert.Equal(updates.GetProperty("channel").GetString(), status.GetProperty("branch").GetString());

        var tasks = status.GetProperty("tasks");
        Assert.Equal(SystemTasks.All.Count, tasks.GetProperty("total").GetInt32());
        Assert.Equal(2, tasks.GetProperty("running").GetInt32());
        Assert.Equal(1, tasks.GetProperty("failing").GetInt32());
    }
}
