using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Filesystem;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Observability;
using Deluno.Integrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;
using Deluno.Jobs;
using Deluno.Movies;
using Deluno.Platform;
using Deluno.Realtime;
using Deluno.Series;
using Deluno.Worker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Deluno.Tray;

// Runs when started as a Windows Service (--service flag).
// Uses WindowsServiceLifetime so the SCM can start/stop/pause the process.
public static class ServiceHost
{
    public static async Task RunAsync(string[] args)
    {
        var settings = AppSettings.Load();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });

        builder.Host.UseWindowsService(opts => opts.ServiceName = "Deluno");
        builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(settings.Port));
        builder.Configuration["Storage:DataRoot"] = settings.DataRoot;

        builder.Services.AddDelunoInfrastructure(builder.Configuration);
        builder.Services.AddDelunoApi();
        builder.Services.AddDelunoPlatformModule();
        builder.Services.AddDelunoMoviesModule();
        builder.Services.AddDelunoSeriesModule();
        builder.Services.AddDelunoJobsModule();
        builder.Services.AddDelunoIntegrationsModule();
        builder.Services.AddDelunoFilesystemModule();
        builder.Services.AddDelunoRealtimeModule();
        builder.Services.AddDelunoWorkerModule();

        builder.Services
            .AddDataProtection()
            .SetApplicationName("Deluno")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataRoot, "protection-keys")));

        var app = builder.Build();

        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"An unexpected error occurred.\"}");
            });
        });

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseDelunoCorrelation();

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
                context.Response.Headers.CacheControl = "no-store";
            await next();
        });

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var requiresAuth =
                (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
                 !path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) &&
                 !path.Equals("/api/auth/bootstrap-status", StringComparison.OrdinalIgnoreCase) &&
                 !path.Equals("/api/auth/bootstrap", StringComparison.OrdinalIgnoreCase) &&
                 !path.StartsWithSegments("/api/health", StringComparison.OrdinalIgnoreCase)) ||
                path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);

            if (!requiresAuth) { await next(); return; }

            var repo = context.RequestServices.GetRequiredService<Deluno.Platform.Data.IPlatformSettingsRepository>();
            var denied = await UserAuthorization.RequireAuthenticatedAsync(context, repo, context.RequestAborted);
            if (denied is not null) { await denied.ExecuteAsync(context); return; }

            var scopeDenied = UserAuthorization.RequireApiScope(context, ResolveScopes(path, context.Request.Method));
            if (scopeDenied is not null) { await scopeDenied.ExecuteAsync(context); return; }

            await next();
        });

        app.MapDelunoApi();
        app.MapDelunoBackupEndpoints();
        app.MapDelunoPlatformEndpoints();
        app.MapDelunoMoviesEndpoints();
        app.MapDelunoSeriesEndpoints();
        app.MapDelunoJobsEndpoints();
        app.MapDelunoDownloadClientIntegrationEndpoints();
        app.MapDelunoSearchEndpoints();
        app.MapDelunoMetadataEndpoints();
        app.MapDelunoFilesystemEndpoints();
        app.MapDelunoRealtime();
        app.MapFallbackToFile("index.html");

        await app.RunAsync();
    }

    private static string[] ResolveScopes(PathString path, string method)
    {
        var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        if (isRead) return ["read"];
        if (path.StartsWithSegments("/api/download-clients", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api/download-dispatches", StringComparison.OrdinalIgnoreCase))
            return ["queue"];
        if (path.StartsWithSegments("/api/filesystem/import", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api/integrations", StringComparison.OrdinalIgnoreCase))
            return ["imports", "queue"];
        if (path.StartsWithSegments("/api/backups", StringComparison.OrdinalIgnoreCase))
            return ["system"];
        return ["write"];
    }
}
