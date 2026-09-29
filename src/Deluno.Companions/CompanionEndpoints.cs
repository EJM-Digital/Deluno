using System.Net;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Companions;

public static class CompanionModule
{
    public static IServiceCollection AddDelunoCompanionsModule(this IServiceCollection services)
    {
        // Downloads of 50-200 MB over whatever connection the owner has.
        services.AddHttpClient(CompanionDownloader.ClientName, client => client.Timeout = TimeSpan.FromMinutes(20));
        services.AddSingleton<ICompanionDownloader, CompanionDownloader>();
        services.AddSingleton<ICompanionSystem, WindowsCompanionSystem>();
        services.AddSingleton<ICompanionLoginStore, SqliteCompanionLoginStore>();
        services.AddSingleton<CompanionToolsService>();
        services.AddSingleton<IDownloadClientTester, DrawerDownloadClientTester>();
        services.AddScoped<DownloadClientConnector>();
        services.AddScoped<ICompanionTool, SabnzbdCompanion>();
        services.AddScoped<ICompanionTool, QbittorrentCompanion>();
        services.AddScoped<ICompanionTool, WeirCompanion>();
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

        group.MapGet("{id}/login", async (string id, [FromServices] ICompanionLoginStore logins, CancellationToken cancellationToken)
            => await logins.GetAsync(id, cancellationToken) is { } login ? Results.Ok(login) : Results.NotFound());

        return endpoints;
    }
}
