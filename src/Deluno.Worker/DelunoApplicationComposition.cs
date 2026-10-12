using Deluno.Companions;
using Deluno.Retention;
using Deluno.Api;
using Deluno.Connections;
using Deluno.Filesystem;
using Deluno.Intake;
using Deluno.Integrations;
using Deluno.Jobs;
using Deluno.Libraries;
using Deluno.Movies;
using Deluno.Notifications;
using Deluno.Platform;
using Deluno.Quality;
using Deluno.Realtime;
using Deluno.Recovery;
using Deluno.Security;
using Deluno.Series;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Worker;

/// <summary>
/// Every module Deluno is made of, registered once.
///
/// <para><b>This exists because there were three copies of it and they had
/// drifted.</b> <c>Deluno.Host</c> composed sixteen modules;
/// <c>DelunoServer</c> (the tray, which is what the Windows installer actually
/// runs) and <c>ServiceHost</c> each composed twelve. The four they were
/// missing were Quality, Connections, Libraries and Recovery — quality
/// profiles, indexers and download clients, libraries, and dispatch recovery.
/// Between them, most of the product.</para>
///
/// <para>The consequence was not a degraded install. It was no install at all:
/// the packaged app threw <c>Unable to resolve service for type
/// 'IDispatchRecoveryHandler' while attempting to activate
/// 'DownloadDispatchPollingService'</c> on startup and never opened a
/// listener. Every automated check passed throughout, because the browser suite
/// drives <c>Deluno.Host</c> and the lab deploys <c>Deluno.Host</c> — the one
/// binary that had the complete list.</para>
///
/// <para>So the list lives here, in the project every host already references,
/// and a module added to Deluno reaches all three by being added once.</para>
/// </summary>
public static class DelunoApplicationComposition
{
    /// <summary>
    /// Registers the whole application. Callers add only what is genuinely
    /// theirs — a web host's rate limiting, the tray's updater — never a
    /// module.
    /// </summary>
    public static IServiceCollection AddDelunoApplicationModules(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The services behind /api itself - readiness, backups, monitoring,
        // the update orchestrator. MapDelunoApplicationEndpoints maps those
        // routes unconditionally, so a host that composes without this maps
        // handlers whose parameters nothing can supply and fails building its
        // own route table. It used to be a separate line each host remembered
        // for itself, which is the shape of drift this file exists to end.
        services.AddDelunoApi();
        services.AddDelunoSecurityModule();
        services.AddDelunoNotificationsModule();
        services.AddDelunoIntakeModule();
        services.AddDelunoPlatformModule();
        services.AddDelunoQualityModule();
        services.AddDelunoConnectionsModule();
        services.AddDelunoLibrariesModule();
        services.AddDelunoMoviesModule();
        services.AddDelunoSeriesModule();
        services.AddDelunoJobsModule();
        // #993: overrides Deluno.Security's no-op default now that Jobs'
        // Activity feed is registered -- see ActivityLocalNetworkAuthChangeNotifier.
        services.AddSingleton<Deluno.Security.Hardening.ILocalNetworkAuthChangeNotifier, Security.ActivityLocalNetworkAuthChangeNotifier>();
        // After the media modules: the composite recovery handler resolves the
        // per-kind components they register.
        services.AddDelunoRecoveryModule();
        services.AddDelunoIntegrationsModule();
        services.AddDelunoFilesystemModule();
        services.AddDelunoRealtimeModule();
        services.AddDelunoWorkerModule();
        services.AddDelunoRetentionModule();
        services.AddDelunoCompanionsModule();
        // #1077: after every module, so every database has its schema when older
        // history rows are linked to their indexers by id.
        services.AddHostedService<Deluno.Infrastructure.Storage.IndexerIdentityBackfillService>();
        // #1076: after the history link-up, which matches by the names history was written under.
        services.AddHostedService<Deluno.Connections.Data.IndexerNamesUpgradeService>();
        // #1174: after every module has its schema, clears an episode wrongly linked to another episode's file.
        services.AddHostedService<Services.EpisodeFileLinkRepairService>();
        services.AddHostedService<Services.MislabelledEpisodeFileRepairService>();
        services.AddScoped<Deluno.Platform.Contracts.IProcessorHandoffSender, Services.ProcessorHandoffSender>();
        services.AddScoped<Deluno.Platform.Contracts.IProcessorHandoffTitleResolver, Services.ProcessorHandoffTitleResolver>();
        services.AddScoped<Deluno.Platform.Contracts.IArtworkRemoteUrls, Services.ArtworkRemoteUrls>();
        services.AddScoped<Companions.InstalledComponentWirer>();
        services.AddScoped<Deluno.Api.Network.IInstalledComponentConnector>(sp => sp.GetRequiredService<Companions.InstalledComponentWirer>());
        services.AddScoped<Deluno.Contracts.IDownloadClientDeletedHook>(sp => sp.GetRequiredService<Companions.InstalledComponentWirer>());
        services.AddScoped<Companions.WeirHealthAddressBackfill>();
        services.AddSingleton<IInstalledClientLogins, Companions.InstalledClientLoginKeeper>();
        services.AddHostedService<Companions.InstalledComponentWiringService>();

        return services;
    }
}
