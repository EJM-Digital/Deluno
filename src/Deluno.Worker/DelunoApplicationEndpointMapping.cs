using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Api.Calendar;
using Deluno.Api.Downloads;
using Deluno.Connections;
using Deluno.Integrations.Subtitles;
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
using Deluno.Worker.Automation;
using Deluno.Series;

namespace Deluno.Worker;

/// <summary>
/// Every endpoint Deluno serves, mapped once.
///
/// <para><b>This lived in Deluno.Host, and the tray kept its own list.</b> The
/// tray - which is what the Windows installer runs - mapped fifteen groups by
/// hand where the host mapped twenty-three, and was missing Libraries,
/// Connections, Quality, Automation, Release Preferences, Guide packages,
/// Migration, Calendar, External integrations and Subtitle providers.</para>
///
/// <para>Worse than absent: the tray's SPA fallback answered them. Every one of
/// those paths returned <c>200</c> with the 1,922-byte <c>index.html</c>, so a
/// client asking for its libraries got the web page and a success code. A POST
/// got 405. The <c>shipped</c> browser suite asserts an unknown <c>/api</c>
/// path returns 404 - and passes, because it drives Deluno.Host.</para>
///
/// <para>So the map lives here, in the project both hosts already reference,
/// beside <see cref="DelunoApplicationComposition"/> for the same reason.</para>
/// </summary>
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
        endpoints.MapDelunoGuidePackageEndpoints();
        endpoints.MapDelunoConnections();
        // Where subtitles come from. Beside the other Connections, because that
        // is what they are (DESIGN-002 rule 4) — the routes live in
        // Deluno.Integrations only because the provider registry does.
        endpoints.MapDelunoSubtitleProviders();
        writeEndpoints.MapDelunoLibraries();
        endpoints.MapDelunoReleasePreferenceEndpoints();
        endpoints.MapDelunoSecurityEndpoints();
        writeEndpoints.MapDelunoNotificationEndpoints();
        writeEndpoints.MapDelunoIntakeEndpoints();
        systemEndpoints.MapDelunoSecretsDiagnostics();
        writeEndpoints.MapDelunoMoviesEndpoints();
        writeEndpoints.MapDelunoSeriesEndpoints();
        writeEndpoints.MapDelunoAutomationEndpoints();
        readEndpoints.MapDelunoAutomationReadEndpoints();
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
