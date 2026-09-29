using System.Runtime.InteropServices;
using Deluno.Api;
using Deluno.Api.Monitoring;
using Deluno.Api.SystemInfo;
using Deluno.Api.Updates;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Deluno.Worker.ApiParity;

/// <summary>Deluno's health at a glance: <c>healthy</c>, or <c>attention</c> when a check failed or an alert is open.</summary>
public sealed record SystemStatusHealth(string Status, int FailedChecks, int OpenAlerts);

public sealed record SystemStatusUpdates(string Channel, bool UpdateAvailable, string? LatestVersion, string State);

public sealed record SystemStatusTasks(int Total, int Running, int Failing);

/// <summary>
/// One call that answers "what is Deluno, which version, how long has it been
/// up, and is it well" (#1020), the way Radarr's and Sonarr's
/// <c>GET /api/v3/system/status</c> does for a dashboard such as Homepage.
///
/// <para>The first block is the field set of Radarr's <c>SystemResource</c>
/// (checked in Radarr.Api.V3/System), in the same camelCase names, so a widget
/// written for Radarr reads it unchanged. Fields Radarr has that Deluno has no
/// counterpart for (<c>isAdmin</c>, <c>packageVersion</c>, <c>packageAuthor</c>,
/// <c>mode</c>, <c>migrationVersion</c>) are left out rather than invented.
/// The second block is what Deluno adds so a single call also carries what used
/// to take three: <c>updates</c> (was <c>/api/updates/status</c>), <c>tasks</c>
/// (was <c>/api/system/tasks</c>) and <c>health</c> (was
/// <c>/api/monitoring/dashboard</c>). Those three still exist and are read from
/// the same services, so no number can differ between them.</para>
/// </summary>
public sealed record SystemStatusResponse(
    string AppName,
    string InstanceName,
    string Version,
    DateTimeOffset BuildTime,
    bool IsDebug,
    bool IsProduction,
    bool IsUserInteractive,
    string StartupPath,
    string AppData,
    string OsName,
    string OsVersion,
    bool IsNetCore,
    bool IsLinux,
    bool IsOsx,
    bool IsWindows,
    bool IsDocker,
    string Branch,
    string DatabaseType,
    string? DatabaseVersion,
    string Authentication,
    string UrlBase,
    string RuntimeVersion,
    string RuntimeName,
    DateTimeOffset StartTime,
    long UptimeSeconds,
    SystemStatusHealth Health,
    SystemStatusUpdates Updates,
    SystemStatusTasks Tasks);

public static class SystemStatusEndpoints
{
    public static IEndpointRouteBuilder MapDelunoSystemStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/system/status", async (
            IOptions<StoragePathOptions> storage,
            IPlatformSettingsRepository settingsRepository,
            IUpdateOrchestrator updates,
            IMonitoringService monitoring,
            IJobQueueRepository jobs,
            IDelunoDatabaseConnectionFactory databases,
            IHostEnvironment environment,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var info = SystemInstallationInfoEndpointRouteBuilderExtensions.Describe(storage.Value.DataRoot, timeProvider);
            var settings = await settingsRepository.GetAsync(cancellationToken);
            var updateStatus = await updates.GetStatusAsync(cancellationToken);
            var dashboard = await monitoring.GetDashboardAsync(cancellationToken);
            var taskStates = await jobs.ListSystemTaskStatesAsync(cancellationToken);

            var startup = AppContext.BaseDirectory;
            var assemblyPath = typeof(DelunoVersion).Assembly.Location;

            return Results.Ok(new SystemStatusResponse(
                AppName: "Deluno",
                InstanceName: Deluno.Contracts.DelunoHostName.Current,
                Version: DelunoVersion.Running,
                BuildTime: string.IsNullOrEmpty(assemblyPath)
                    ? info.ProcessStartUtc
                    : new DateTimeOffset(File.GetLastWriteTimeUtc(assemblyPath), TimeSpan.Zero),
                IsDebug: environment.IsDevelopment(),
                IsProduction: environment.IsProduction(),
                IsUserInteractive: Environment.UserInteractive,
                StartupPath: startup,
                AppData: info.AppDataPath,
                OsName: OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : RuntimeInformation.OSDescription,
                OsVersion: Environment.OSVersion.Version.ToString(),
                IsNetCore: true,
                IsLinux: OperatingSystem.IsLinux(),
                IsOsx: OperatingSystem.IsMacOS(),
                IsWindows: OperatingSystem.IsWindows(),
                IsDocker: IsDocker(),
                Branch: updateStatus.Channel,
                DatabaseType: "sqLite",
                DatabaseVersion: await ReadSqliteVersionAsync(databases, cancellationToken),
                Authentication: "forms",
                UrlBase: string.Empty,
                RuntimeVersion: Environment.Version.ToString(),
                RuntimeName: ".NET",
                StartTime: info.ProcessStartUtc,
                UptimeSeconds: info.UptimeSeconds,
                Health: new SystemStatusHealth(
                    dashboard.Readiness.Ready && dashboard.Alerts.Count == 0 ? "healthy" : "attention",
                    dashboard.Readiness.FailedChecks,
                    dashboard.Alerts.Count),
                Updates: new SystemStatusUpdates(
                    updateStatus.Channel,
                    updateStatus.UpdateAvailable,
                    updateStatus.LatestVersion,
                    updateStatus.State),
                Tasks: new SystemStatusTasks(
                    SystemTasks.All.Count,
                    taskStates.Count(state => string.Equals(state.LastResult, "running", StringComparison.OrdinalIgnoreCase)),
                    taskStates.Count(state => string.Equals(state.LastResult, "failed", StringComparison.OrdinalIgnoreCase)))));
        }).RequireAuthorization(DelunoAuthorizationPolicies.Read);

        return endpoints;
    }

    private static bool IsDocker()
        => string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
           || File.Exists("/.dockerenv");

    private static async Task<string?> ReadSqliteVersionAsync(IDelunoDatabaseConnectionFactory databases, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version();";
            return (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
