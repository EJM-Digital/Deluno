using Deluno.Api;
using Deluno.Api.Network;
using Deluno.Api.Backup;
using Deluno.Api.Updates;
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
using Microsoft.AspNetCore.DataProtection;
using UserAuthorization = Deluno.Security.UserAuthorization;

namespace Deluno.Tray;

public sealed class DelunoServer : IDisposable
{
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    public int? ListeningPort { get; private set; }
    public IReadOnlyList<string> ReachableUrls { get; private set; } = Array.Empty<string>();
    public LanAccessStatus? LanAccess { get; private set; }

    /// <summary>Deluno's firewall rule for the port it is listening on (#623).</summary>
    public WindowsFirewallLanAccess? Firewall { get; private set; }

    /// <summary>Optional port override (from --port CLI arg). Takes precedence over AppSettings.Port.</summary>
    public static int? PortOverride { get; set; }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var settings = AppSettings.Load();
        var port = PortOverride ?? settings.Port;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });

        builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(port));

        // Override config values with resolved Windows paths
        builder.Configuration["Storage:DataRoot"] = settings.DataRoot;
        // NOTE: do NOT also set Kestrel:EndPoints:Http:Url — that adds a SECOND
        // listener on the same port, which on Windows fails with WSAEADDRINUSE
        // because [::]:port and http://+:port overlap. The explicit
        // ListenAnyIP() above is sufficient.

        builder.Services.AddSingleton<IUpdateOrchestrator, VelopackUpdateOrchestrator>();
        Firewall = new WindowsFirewallLanAccess(port);
        builder.Services.AddSingleton<ILanAccess>(Firewall);
        builder.Services.AddHostedService<TrayUpdateBackgroundService>();
        builder.Services.AddDelunoInfrastructure(builder.Configuration);
        builder.Services.AddDelunoApplicationModules();
        builder.Services.AddHostedService<ImportRecoveryCleanupService>();

        builder.Services
            .AddDataProtection()
            .SetApplicationName("Deluno")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataRoot, "protection-keys")));

        // Protect local integration credentials and platform secrets.
        builder.Services.AddDelunoPlatformSecrets(
            Path.Combine(settings.DataRoot, "secrets", "master.key"));

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

            var denied = await UserAuthorization.RequireAuthenticatedAsync(context, context.RequestAborted);
            if (denied is not null) { await denied.ExecuteAsync(context); return; }

            var scopeDenied = UserAuthorization.RequireApiScope(context, ResolveScopes(path, context.Request.Method));
            if (scopeDenied is not null) { await scopeDenied.ExecuteAsync(context); return; }

            await next();
        });

        // The same map Deluno.Host uses. This was fifteen hand-maintained
        // calls where the host made twenty-three, so the installed app had no
        // libraries, indexers, download clients, quality profiles, automation
        // or release preferences at all - and the fallback below answered
        // those paths with the web page and a 200.
        _app.MapDelunoApplicationEndpoints();

        _app.MapFallback(async context =>
        {
            // An unknown API path is a 404, not the app shell. Serving
            // index.html here makes a broken client call look like a
            // successful page load, and hides the real fault - which is
            // exactly how the missing routes above went unnoticed.
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var indexPath = Path.Combine(
                _app.Environment.WebRootPath ?? _app.Environment.ContentRootPath,
                "index.html");

            // SendFileAsync does not infer a content type and Deluno sends
            // nosniff, so without this the browser renders index.html as
            // plain text.
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(indexPath);
        });

        await _app.StartAsync(_cts.Token);

        ListeningPort = port;
        ReachableUrls = NetworkAccess.GetReachableUrls(port);
        await EnsureFirewallRuleAndLogAsync(port, settings.DataRoot, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads whether the firewall lets other devices reach Deluno, then writes a
    /// startup-log entry with every address Deluno answers on. It only looks:
    /// adding the rule asks Windows for permission, which happens once after
    /// install and from the tray menu or System, never on its own at startup.
    /// Failures here never fail the start: the server is already running.
    /// </summary>
    private async Task EnsureFirewallRuleAndLogAsync(int port, string dataRoot, CancellationToken ct)
    {
        try
        {
            LanAccess = await Firewall!.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LanAccess = new LanAccessStatus(LanAccessStates.Blocked, port, WindowsFirewallLanAccess.RuleNameFor(port), ex.Message, CanAllow: true);
        }

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Deluno", "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "tray-startup.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{DateTimeOffset.Now:O} Deluno started on port {port}");
            sb.AppendLine($"  Data root: {dataRoot}");
            sb.AppendLine("  Reachable at:");
            foreach (var url in ReachableUrls) sb.AppendLine($"    {url}");
            sb.Append("  Firewall: ");
            sb.AppendLine(LanAccess is null ? "(not checked)" : $"{LanAccess.State}: {LanAccess.Message}");
            sb.AppendLine();
            File.AppendAllText(logPath, sb.ToString());
        }
        catch
        {
            // Best-effort logging.
        }
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
