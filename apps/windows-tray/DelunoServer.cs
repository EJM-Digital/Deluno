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
using Deluno.Worker.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using UserAuthorization = Deluno.Security.UserAuthorization;

namespace Deluno.Tray;

public sealed class DelunoServer : IDisposable
{
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private volatile bool _stopRequested;

    /// <summary>Raised when the web host stopped and nobody asked it to (a critical background service gave up, #1080).</summary>
    public event Action? StoppedUnexpectedly;
    public int? ListeningPort { get; private set; }
    public IReadOnlyList<string> ReachableUrls { get; private set; } = Array.Empty<string>();
    public LanAccessStatus? LanAccess { get; private set; }

    /// <summary>Deluno's firewall rule for the port it is listening on (#623).</summary>
    public WindowsFirewallLanAccess? Firewall { get; private set; }

    /// <summary>Optional port override (from --port CLI arg). Takes precedence over AppSettings.Port.</summary>
    public static int? PortOverride { get; set; }

    public async Task StartAsync()
    {
        _stopRequested = false;
        if (_app is not null)
        {
            // A host that stopped by itself is still held here; let it go before starting a new one.
            try { await _app.DisposeAsync(); } catch { /* already stopped */ }
            _app = null;
        }

        _cts?.Dispose();
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

        // The tray's "Let any address in" recovery action (#939): the same
        // config override docs/security-allowed-hosts.md documents, written
        // here instead of by hand so the next restart honours it without a
        // working web UI. AllowedHostsSettingsCache checks this key on every
        // request, ahead of whatever is saved in the database.
        if (settings.AllowedHostsForceDisabled)
        {
            builder.Configuration["Security:AllowedHosts:ForceDisabled"] = "true";
        }
        // NOTE: do NOT also set Kestrel:EndPoints:Http:Url — that adds a SECOND
        // listener on the same port, which on Windows fails with WSAEADDRINUSE
        // because [::]:port and http://+:port overlap. The explicit
        // ListenAnyIP() above is sufficient.

        // Allowed Hosts (#939): the same forwarding configuration
        // Deluno.Host uses, so a reverse proxy in front of the Windows
        // install is honoured too. See Deluno.Host/Program.cs for why
        // KnownIPNetworks/KnownProxies are cleared rather than left at their
        // loopback-only default.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        // The tray restarts the host itself (HostRestartSupervisor), so giving up
        // must not leave a failing exit code behind for the tray process.
        builder.Services.AddSingleton(new BackgroundServiceSafetyOptions { SetExitCodeWhenGivingUp = false });
        builder.Services.AddSingleton<IUpdateOrchestrator, VelopackUpdateOrchestrator>();
        Firewall = new WindowsFirewallLanAccess(port);
        builder.Services.AddSingleton<ILanAccess>(Firewall);
        // Without this, DelunoApiExtensions' TryAddSingleton default
        // (UncheckedBundledComponentDetection) wins, and GET
        // /api/system/bundled-components reports every component as neither
        // installed nor reachable on every real Windows install -- the
        // installer's whole "detected on this machine" pre-fill (#640) goes
        // dark even when qBittorrent or Weir is sitting right there. Only
        // Deluno.Host's separate entry point registered the Windows
        // implementation; the tray's own server never did.
        builder.Services.AddSingleton<IBundledComponentDetection, WindowsBundledComponentDetection>();
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

        _app.UseForwardedHeaders();

        // Allowed Hosts (#939): the same enforcement Deluno.Host applies,
        // ahead of routing so it covers the UI, the API, SignalR/websocket
        // upgrades and the external integration API alike. Off by default
        // and never turned on by an update (#936).
        _app.UseDelunoAllowedHosts();

        _app.UseDefaultFiles();
        _app.UseStaticFiles();
        _app.UseDelunoCorrelation();

        // Routing first, so the sign-in check below can see which endpoint a
        // request is for and let a public one (the poster route, #1146) through.
        _app.UseRouting();

        // An explicit UseRouting turns off the authentication and authorization
        // steps WebApplication adds by itself, and every endpoint carrying
        // authorization metadata then fails with a 500. Deluno.Host adds them
        // the same way.
        _app.UseAuthentication();
        _app.UseAuthorization();

        _app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            // Posters are long-cached files; no-store would stop the browser
            // keeping them, exactly as in Deluno.Host.
            if ((path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
                 !path.StartsWithSegments("/api/metadata/artwork", StringComparison.OrdinalIgnoreCase)) ||
                path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.CacheControl = "no-store";
            }
            await next();
        });

        _app.UseDelunoSignInGate(ResolveScopes);

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

        var startedApp = _app;
        // StopApplication() only cancels a token here (nothing waits on WaitForShutdown), so
        // the guard stops the host itself, force-disposing it if it will not stop in time.
        UnexpectedStopGuard.Attach(
            startedApp,
            () => _stopRequested || !ReferenceEquals(_app, startedApp),
            () => StoppedUnexpectedly?.Invoke(),
            TimeSpan.FromSeconds(15));

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
        _stopRequested = true;
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
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Import recovery cleanup error.");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
