using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Filesystem;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Observability;
using Deluno.Integrations;
using Deluno.Intake;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;
using Deluno.Jobs;
using Deluno.Movies;
using Deluno.Notifications;
using Deluno.Platform;
using Deluno.Security;
using Deluno.Security.Hardening;
using Deluno.Realtime;
using Deluno.Series;
using Deluno.Worker;
using Deluno.Worker.Services;
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

        // Same recovery override as DelunoServer.cs (#939): a service
        // install honours the tray's "Let any address in" action too, since
        // running as a service is exactly when there is no tray icon to fall
        // back on.
        if (settings.AllowedHostsForceDisabled)
        {
            builder.Configuration["Security:AllowedHosts:ForceDisabled"] = "true";
        }

        // When a critical background service cannot be kept running, end the
        // process with a failing exit (#1080). A graceful host stop is reported
        // to the service manager as a clean stop, so its restart actions would
        // never run; a process that dies while the service is Running does.
        var safetyOptions = new BackgroundServiceSafetyOptions
        {
            ExitProcess = code => Environment.Exit(code)
        };
        builder.Services.AddSingleton(safetyOptions);

        builder.Services.AddDelunoInfrastructure(builder.Configuration);
        builder.Services.AddDelunoApplicationModules();

        builder.Services
            .AddDataProtection()
            .SetApplicationName("Deluno")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataRoot, "protection-keys")));

        // Protect local integration credentials and platform secrets.
        builder.Services.AddDelunoPlatformSecrets(
            Path.Combine(settings.DataRoot, "secrets", "master.key"));

        var app = builder.Build();

        // The Windows service lifetime reports its ServiceBase.ExitCode (default 0) to the
        // service manager when the host stops. On a give-up set it non-zero first, so the
        // stop counts as a failure (sc failureflag 1) and the restart actions run (#1080).
        safetyOptions.SetServiceExitCode = code =>
        {
            if (app.Services.GetService<Microsoft.Extensions.Hosting.IHostLifetime>() is System.ServiceProcess.ServiceBase serviceBase)
            {
                serviceBase.ExitCode = code;
            }
        };

        // Services installed by earlier builds have no restart-on-failure actions; set
        // them again on every start (sc failure is idempotent). Off the start path, once
        // Deluno is up, so three slow sc.exe calls can never eat the service manager's
        // 30 seconds to start; a refusal is logged and shown on Health (#1080).
        app.Lifetime.ApplicationStarted.Register(() => _ = ServiceManager.EnsureFailureActionsInBackground(
            message => app.Logger.LogWarning("{Message}", message),
            app.Services.GetService<Deluno.Contracts.IBackgroundProblemReporter>()));

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

            var denied = await UserAuthorization.RequireAuthenticatedAsync(context, context.RequestAborted);
            if (denied is not null) { await denied.ExecuteAsync(context); return; }

            var scopeDenied = UserAuthorization.RequireApiScope(context, ResolveScopes(path, context.Request.Method));
            if (scopeDenied is not null) { await scopeDenied.ExecuteAsync(context); return; }

            await next();
        });

        // The same map every other host uses. See DelunoApplicationEndpointMapping.
        app.MapDelunoApplicationEndpoints();

        app.MapFallback(async context =>
        {
            // An unknown API path is a 404, not the app shell.
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var indexPath = Path.Combine(
                app.Environment.WebRootPath ?? app.Environment.ContentRootPath,
                "index.html");
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(indexPath);
        });

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
