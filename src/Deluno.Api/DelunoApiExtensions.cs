using Deluno.Api.BlockedReleases;
using Deluno.Contracts.Manifest;
using Deluno.Api.Backup;
using Deluno.Api.Downloads;
using Deluno.Api.Health;
using Deluno.Api.IndexerHistory;
using Deluno.Api.Monitoring;
using Deluno.Api.Network;
using Deluno.Api.SystemInfo;
using Deluno.Api.Updates;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using Deluno.Platform.Migration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Deluno.Api;

public static class DelunoApiExtensions
{
    public static IServiceCollection AddDelunoApi(this IServiceCollection services)
    {
        services.AddRouting();
        services.AddSingleton<DelunoBackupService>();
        services.AddSingleton<IDelunoBackupService>(sp => sp.GetRequiredService<DelunoBackupService>());
        services.AddSingleton<IMigrationBackupService>(sp => sp.GetRequiredService<DelunoBackupService>());
        services.AddHostedService(sp => sp.GetRequiredService<DelunoBackupService>());
        services.AddSingleton<IDelunoReadinessService, DelunoReadinessService>();
        services.AddSingleton<DatabaseIntegrityService>();
        services.AddHostedService(sp => sp.GetRequiredService<DatabaseIntegrityService>());
        services.AddHostedService<SystemLogPushService>();
        services.AddSingleton<IApiLatencyTracker, InMemoryApiLatencyTracker>();
        services.AddSingleton<IMonitoringService, MonitoringService>();
        services.AddSingleton<DashboardMetricsCache>();
        services.AddSingleton<Deluno.Api.Monitoring.LibraryRootReachability>();
        services.TryAddSingleton<IReleaseNotesProvider, EmbeddedReleaseNotesProvider>();
        services.TryAddSingleton<IUpdateOrchestrator, DefaultUpdateOrchestrator>();
        services.TryAddSingleton<ILanAccess, UncheckedLanAccess>();
        services.TryAddSingleton<IBundledComponentDetection, UncheckedBundledComponentDetection>();
        services.TryAddSingleton<IComponentRecoveryNotices, NoComponentRecoveryNotices>();
        services.TryAddSingleton<IBundledComponentCredentialStore>(sp => BundledComponentCredentialStores.CreateForDataRoot(
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Storage:DataRoot"] ?? "data"));
        services.TryAddSingleton<IInstalledComponentRecord>(sp => new FileInstalledComponentRecord(FileInstalledComponentRecord.PathFor(
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Storage:DataRoot"] ?? "data")));
        services.AddSingleton<IIndexerHistoryEventStore, SqliteIndexerHistoryEventStore>();
        services.AddSingleton<IIndexerHistoryService, IndexerHistoryService>();
        services.AddSingleton<Deluno.Contracts.IIndexerAuthFailureRecorder, IndexerHistoryAuthFailureRecorder>();
        services.AddSingleton<Deluno.Contracts.IIndexerHistoryRecorder, IndexerHistoryRecorder>();
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoApi(
        this IEndpointRouteBuilder endpoints,
        bool includeOperationalEndpoints = true)
    {
        endpoints.MapGet("/health", () => Results.Ok(DelunoReadinessService.Live()))
            .AllowAnonymous()
            .WithMetadata(new DelunoPublicEndpointAttribute());

        var api = endpoints.MapGroup("/api");

        api.MapGet("/", () => Results.Ok(new
        {
            name = "Deluno",
            kind = "media-automation",
            architecture = "sqlite-first modular monolith"
        }));

        api.MapGet("/health/live", () => Results.Ok(DelunoReadinessService.Live()))
            .AllowAnonymous()
            .WithMetadata(new DelunoPublicEndpointAttribute());

        api.MapGet("/health/ready", async (
            IDelunoReadinessService readiness,
            CancellationToken cancellationToken) =>
        {
            var result = await readiness.CheckAsync(cancellationToken);
            return Results.Json(
                result,
                statusCode: result.Ready
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable);
        })
            .AllowAnonymous()
            .WithMetadata(new DelunoPublicEndpointAttribute());

        api.MapDelunoMonitoringEndpoints();
        api.MapDelunoLanAccessEndpoints();
        api.MapDelunoConnectivityEndpoints();
        api.MapDelunoBundledComponentsEndpoints();
        api.MapDelunoComponentRecoveryEndpoints();
        api.MapDelunoBundledComponentCredentialsEndpoints();
        api.MapDelunoSystemLogLevelEndpoints();
        api.MapDelunoSystemLogFilesEndpoints();
        api.MapDelunoSystemLogAndDatabasesEndpoints();
        api.MapDelunoSystemInstallationInfoEndpoints();

        if (includeOperationalEndpoints)
        {
            endpoints.MapDownloadDispatchesEndpoints();
        }

        endpoints.MapDelunoDashboardMetricsEndpoints();
        endpoints.MapDelunoBlockedReleaseEndpoints();
        endpoints.MapDelunoImportFailureRuleEndpoints();
        endpoints.MapDelunoIndexerHistoryEndpoints();
        endpoints.MapDelunoSubtitleWantedEndpoints();

        return endpoints;
    }
}
