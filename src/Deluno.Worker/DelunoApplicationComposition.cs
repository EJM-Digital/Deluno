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
        // After the media modules: the composite recovery handler resolves the
        // per-kind components they register.
        services.AddDelunoRecoveryModule();
        services.AddDelunoIntegrationsModule();
        services.AddDelunoFilesystemModule();
        services.AddDelunoRealtimeModule();
        services.AddDelunoWorkerModule();

        return services;
    }
}
