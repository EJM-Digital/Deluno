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
using UserAuthorization = Deluno.Platform.UserAuthorization;

namespace Deluno.Tray;

public sealed class DelunoServer : IDisposable
{
    private WebApplication? _app;
    private CancellationTokenSource? _cts;

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var settings = AppSettings.Load();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });

        builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(settings.Port));

        // Override config values with resolved Windows paths
        builder.Configuration["Storage:DataRoot"] = settings.DataRoot;
        builder.Configuration["Kestrel:EndPoints:Http:Url"] = $"http://+:{settings.Port}";

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
        builder.Services.AddHostedService<ImportRecoveryCleanupService>();

        builder.Services
            .AddDataProtection()
            .SetApplicationName("Deluno")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataRoot, "protection-keys")));

        _app = builder.Build();

        _app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"An unexpected error occurred.\"}");
            });
        });

        _app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            await next();
        });

        _app.UseDefaultFiles();
        _app.UseStaticFiles();
        _app.UseDelunoCorrelation();

        _app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.CacheControl = "no-store";
            }
            await next();
        });

        _app.Use(async (context, next) =>
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

        _app.MapDelunoApi();
        _app.MapDelunoBackupEndpoints();
        _app.MapDelunoPlatformEndpoints();
        _app.MapDelunoMoviesEndpoints();
        _app.MapDelunoSeriesEndpoints();
        _app.MapDelunoJobsEndpoints();
        _app.MapDelunoDownloadClientIntegrationEndpoints();
        _app.MapDelunoSearchEndpoints();
        _app.MapDelunoMetadataEndpoints();
        _app.MapDelunoFilesystemEndpoints();
        _app.MapDelunoRealtime();
        _app.MapFallbackToFile("index.html");

        await _app.StartAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        await _cts!.CancelAsync();
        await _app.StopAsync();
        _app = null;
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

// Mirrors the service in Deluno.Host — needed here since Tray doesn't reference Host.
// This is an intentional duplication; the correct long-term fix is to move this
// service into Deluno.Worker.
internal sealed class ImportRecoveryCleanupService(
    Deluno.Movies.Data.IMovieCatalogRepository movieRepository,
    Deluno.Series.Data.ISeriesCatalogRepository seriesRepository,
    TimeProvider timeProvider,
    ILogger<ImportRecoveryCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cutoff     = timeProvider.GetUtcNow() - RetentionPeriod;
                var movieCount  = await movieRepository.CleanupImportRecoveryCasesAsync(cutoff, stoppingToken);
                var seriesCount = await seriesRepository.CleanupImportRecoveryCasesAsync(cutoff, stoppingToken);
                if (movieCount > 0 || seriesCount > 0)
                    logger.LogInformation(
                        "Import recovery cleanup: {M} movie and {S} series cases removed.",
                        movieCount, seriesCount);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Import recovery cleanup error.");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
