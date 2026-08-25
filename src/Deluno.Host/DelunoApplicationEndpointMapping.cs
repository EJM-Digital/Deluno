using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Api.Calendar;
using Deluno.Api.Downloads;
using Deluno.Connections;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;
using Deluno.Intake;
using Deluno.Jobs;
using Deluno.Libraries;
using Deluno.Movies;
using Deluno.Notifications;
using Deluno.Platform;
using Deluno.Quality;
using Deluno.Realtime;
using Deluno.Security;
using Deluno.Security.Hardening;
using Deluno.Series;

namespace Deluno.Host;

public static class DelunoApplicationEndpointMapping
{
    public static IEndpointRouteBuilder MapDelunoApplicationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var writeEndpoints = endpoints.MapGroup(string.Empty)
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);
        var queueEndpoints = endpoints.MapGroup(string.Empty)
            .RequireAuthorization(DelunoAuthorizationPolicies.Queue);
        var importsEndpoints = endpoints.MapGroup(string.Empty)
            .RequireAuthorization(DelunoAuthorizationPolicies.Imports);
        var systemEndpoints = endpoints.MapGroup(string.Empty)
            .RequireAuthorization(DelunoAuthorizationPolicies.System);
        var readEndpoints = endpoints.MapGroup(string.Empty)
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        writeEndpoints.MapDelunoApi(includeOperationalEndpoints: false);
        systemEndpoints.MapDelunoBackupEndpoints();
        // Mapped outside the authorization groups: the feed authenticates
        // itself, because a calendar client cannot send a header (#260).
        endpoints.MapDelunoCalendarFeedEndpoints();
        endpoints.MapDelunoPlatformEndpoints();
        endpoints.MapDelunoMigrationEndpoints();
        writeEndpoints.MapDelunoLibraryActionEndpoints();
        endpoints.MapDelunoExternalIntegrationEndpoints();
        writeEndpoints.MapDelunoQuality();
        endpoints.MapDelunoConnections();
        writeEndpoints.MapDelunoLibraries();
        endpoints.MapDelunoSecurityEndpoints();
        writeEndpoints.MapDelunoNotificationEndpoints();
        writeEndpoints.MapDelunoIntakeEndpoints();
        systemEndpoints.MapDelunoSecretsDiagnostics();
        writeEndpoints.MapDelunoMoviesEndpoints();
        writeEndpoints.MapDelunoSeriesEndpoints();
        writeEndpoints.MapDelunoJobsEndpoints();
        queueEndpoints.MapDelunoDownloadClientIntegrationEndpoints();
        writeEndpoints.MapDelunoSearchEndpoints();
        endpoints.MapDelunoMetadataEndpoints();
        importsEndpoints.MapDelunoFilesystemEndpoints();
        queueEndpoints.MapDownloadDispatchesEndpoints();
        readEndpoints.MapDelunoRealtime();
        return endpoints;
    }
}
