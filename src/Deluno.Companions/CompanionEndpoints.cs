using System.Net;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Companions;

public static class CompanionModule
{
    public static IServiceCollection AddDelunoCompanionsModule(this IServiceCollection services)
    {
        // Downloads of 50-200 MB over whatever connection the owner has.
        services.AddHttpClient(CompanionDownloader.ClientName, client => client.Timeout = TimeSpan.FromMinutes(20))
            .AddHttpMessageHandler(() => new Deluno.Infrastructure.Http.GitHubApiConditionalHandler());
        services.AddSingleton<ICompanionDownloader, CompanionDownloader>();
        // #1117: where a processor connection that reports a version finds its newest release.
        services.AddSingleton<Deluno.Connections.Versions.ReleaseFeed>(
            new Deluno.Connections.Versions.GitHubReleasesFeed(Deluno.Connections.Versions.ConnectionProjects.Weir, CompanionCatalog.WeirRepository, IncludePreReleases: true));
        services.AddSingleton<ICompanionSystem, WindowsCompanionSystem>();
        services.AddSingleton<ICompanionLoginCleanup, SqliteCompanionLoginCleanup>();
        services.TryAddSingleton<IInstalledClientLogins, NoInstalledClientLogins>();
        services.AddSingleton<CompanionToolsService>();
        services.AddSingleton<IDownloadClientTester, DrawerDownloadClientTester>();
        services.AddScoped<DownloadClientConnector>();
        services.AddScoped<ICompanionTool, SabnzbdCompanion>();
        services.AddScoped<ICompanionTool, QbittorrentCompanion>();
        services.AddScoped<ConnectionUpdateService>();
        services.AddScoped<WeirCompanion>();
        services.AddScoped<ICompanionTool>(sp => sp.GetRequiredService<WeirCompanion>());
        services.AddScoped<IWeirConnector>(sp => sp.GetRequiredService<WeirCompanion>());
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoCompanions(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/companion-tools").RequireAuthorization(DelunoAuthorizationPolicies.System);

        group.MapGet(string.Empty, async ([FromServices] CompanionToolsService service, CancellationToken cancellationToken)
            => Results.Ok(new CompanionToolsResponse(CompanionToolsService.Availability(), await service.ListAsync(cancellationToken), CompanionToolsService.UninstallNote)));

        group.MapPost("install", (
            HttpContext httpContext,
            [FromBody] CompanionInstallRequest request,
            [FromServices] CompanionToolsService service) =>
        {
            var availability = CompanionToolsService.Availability();
            if (!availability.Available)
            {
                return Results.Conflict(new { message = availability.Reason });
            }

            // Windows' permission prompt appears on this computer's screen. Asked
            // for from another device, nobody would be there to answer it.
            var remote = httpContext.Connection.RemoteIpAddress;
            if (remote is not null && !IPAddress.IsLoopback(remote))
            {
                return Results.Conflict(new { message = "Open Deluno on the computer it runs on to install tools: Windows asks for permission on that screen." });
            }

            var ids = (request.ToolIds ?? []).Where(id => CompanionToolIds.All.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (ids.Length == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["toolIds"] = ["Tick at least one tool."] });
            }

            var baseUrl = $"{httpContext.Request.Scheme}://127.0.0.1:{httpContext.Connection.LocalPort}{httpContext.Request.PathBase}";
            return service.TryStart(ids, baseUrl)
                ? Results.Accepted("/api/companion-tools")
                : Results.Conflict(new { message = "Tools are already being set up. Wait for that to finish." });
        });

        // The apps only the tray's installer can set up (Transmission, Deluge, NZBGet): the tray is a separate
        // process, so the ask goes to it as a file in the data root, and it opens its picker for them.
        group.MapPost("tray-install", (
            HttpContext httpContext,
            [FromBody] CompanionInstallRequest request,
            [FromServices] Microsoft.Extensions.Options.IOptions<Deluno.Infrastructure.Storage.StoragePathOptions> storage) =>
        {
            var availability = CompanionToolsService.Availability();
            if (!availability.Available)
            {
                return Results.Conflict(new { message = availability.Reason });
            }

            var remote = httpContext.Connection.RemoteIpAddress;
            if (remote is not null && !IPAddress.IsLoopback(remote))
            {
                return Results.Conflict(new { message = "Open Deluno on the computer it runs on to install apps: Windows asks for permission on that screen." });
            }

            var ids = (request.ToolIds ?? []).Where(id => CompanionToolIds.TrayInstalled.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (ids.Length == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["toolIds"] = ["Tick at least one app."] });
            }

            try
            {
                Deluno.Contracts.PendingComponentInstalls.Add(storage.Value.DataRoot, ids, retryAfterUtc: null, askedByPerson: true, reason: "You asked for it on the Companion Tools page.");
            }
            catch (Deluno.Contracts.StateFileException exception)
            {
                // The ask was not stored (#1260): never answer "accepted" for something the tray will never see.
                return Results.Conflict(new { message = exception.Message });
            }

            return Results.Accepted("/api/companion-tools");
        });

        // #1118: a newer version of a connected app is a notice you can act on.
        var updates = endpoints.MapGroup("/api/connections").RequireAuthorization(DelunoAuthorizationPolicies.System);

        updates.MapGet("updates", async ([FromServices] ConnectionUpdateService service, CancellationToken cancellationToken)
            => Results.Ok(await service.ListAsync(cancellationToken)));

        updates.MapPost("{kind}/{id}/version/dismiss", async (
            string kind,
            string id,
            [FromServices] ConnectionUpdateService service,
            CancellationToken cancellationToken)
            => await service.DismissAsync(kind, id, cancellationToken) is { } dismissed ? Results.Ok(new { dismissed }) : Results.NotFound());

        updates.MapPost("{kind}/{id}/update", async (
            HttpContext httpContext,
            string kind,
            string id,
            [FromServices] ConnectionUpdateService service,
            CancellationToken cancellationToken) =>
        {
            var availability = CompanionToolsService.Availability();
            if (!availability.Available)
            {
                return Results.Conflict(new { message = availability.Reason });
            }

            // Windows' permission prompt appears on this computer's screen. Asked for from another device, nobody
            // would be there to answer it.
            var remote = httpContext.Connection.RemoteIpAddress;
            if (remote is not null && !IPAddress.IsLoopback(remote))
            {
                return Results.Conflict(new { message = "Open Deluno on the computer it runs on to update: Windows asks for permission on that screen." });
            }

            var baseUrl = $"{httpContext.Request.Scheme}://127.0.0.1:{httpContext.Connection.LocalPort}{httpContext.Request.PathBase}";
            return Results.Ok(await service.UpdateNowAsync(kind, id, baseUrl, cancellationToken));
        });

        return endpoints;
    }
}
