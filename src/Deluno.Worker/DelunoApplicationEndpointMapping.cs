using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Api.Calendar;
using Deluno.Api.Discover;
using Deluno.Api.Downloads;
using Deluno.Api.Health;
using Deluno.Connections;
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.MediaServers;
using Deluno.Integrations.Seerr;
using Deluno.Integrations.Trakt;
using Deluno.Retention;
using Deluno.Companions;
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
using Deluno.Worker.ApiParity;
using Deluno.Worker.Automation;
using Deluno.Worker.Attention;
using Deluno.Worker.Dashboard;
using Deluno.Worker.SystemReadings;
using Deluno.Series;
using Deluno.Movies.Testing;
using Deluno.Worker.Testing;

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
        readEndpoints.MapDelunoHealthCheckEndpoints();
        readEndpoints.MapDelunoAttentionReadEndpoints();
        writeEndpoints.MapDelunoAttentionWriteEndpoints();
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
        endpoints.MapDelunoMediaServers();
        endpoints.MapDelunoSeerr();
        endpoints.MapDelunoTrakt();
        endpoints.MapDelunoExternalFileChanged();
        endpoints.MapDelunoExternalRedownload();
        endpoints.MapDelunoCommandEndpoints();
        endpoints.MapDelunoSystemStatusEndpoints();
        endpoints.MapDelunoDiskSpaceEndpoints();
        endpoints.MapDelunoQueueHistoryWantedEndpoints();
        endpoints.MapDelunoMediaLibraryMembership();
        endpoints.MapDelunoRetention();
        endpoints.MapDelunoCompanions();
        writeEndpoints.MapDelunoLibraries();
        endpoints.MapDelunoReleasePreferenceEndpoints();
        endpoints.MapDelunoSecurityEndpoints();
        writeEndpoints.MapDelunoNotificationEndpoints();
        writeEndpoints.MapDelunoIntakeEndpoints();
        writeEndpoints.MapDelunoMoviesEndpoints();
        // Mapped from Deluno.Filesystem, not beside the rest of /api/movies:
        // it needs ISubtitleInventoryService, and Deluno.Movies cannot depend
        // on Deluno.Filesystem, which already depends on Deluno.Movies (#782).
        writeEndpoints.MapDelunoMovieFilesEndpoints();
        writeEndpoints.MapDelunoEpisodeFilesEndpoints();
        writeEndpoints.MapDelunoSeriesEndpoints();
        writeEndpoints.MapDelunoDiscoverEndpoints();
        writeEndpoints.MapDelunoAutomationEndpoints();
        readEndpoints.MapDelunoAutomationReadEndpoints();
        readEndpoints.MapDelunoDashboardSummaryEndpoints();
        readEndpoints.MapDelunoSystemOverviewEndpoints();
        writeEndpoints.MapDelunoJobsEndpoints();
        queueEndpoints.MapDelunoDownloadClientIntegrationEndpoints();
        queueEndpoints.MapDelunoKeepSeedingEndpoints();
        writeEndpoints.MapDelunoSearchEndpoints();
        writeEndpoints.MapDelunoManualSearchEndpoints();
        endpoints.MapDelunoMetadataEndpoints();
        importsEndpoints.MapDelunoFilesystemEndpoints();
        queueEndpoints.MapDownloadDispatchesEndpoints();
        readEndpoints.MapDelunoRealtime();

        // #934 — never mapped unless a smoke process explicitly opts in. See
        // MoviesLargeLibrarySeedEndpoints's own doc comment for why this is
        // safe: the shipped app's configuration has no
        // Testing:EnableLargeLibrarySeedEndpoint key, so it reads false and
        // the route is never added to the shipped binary's endpoint table —
        // not merely unauthorized, absent.
        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        if (configuration.GetValue<bool>("Testing:EnableLargeLibrarySeedEndpoint"))
        {
            writeEndpoints.MapDelunoMoviesLargeLibrarySeedEndpoints();
        }

        // #1178 — same guard: lets a smoke test make the server raise realtime events.
        if (configuration.GetValue<bool>("Testing:EnableRealtimePushEndpoint"))
        {
            writeEndpoints.MapDelunoRealtimePushTestEndpoints();
        }

        return endpoints;
    }
}
