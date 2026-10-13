using Deluno.Api;
using Deluno.Api.Network;
using Deluno.Api.Backup;
using Deluno.Api.Updates;
using Deluno.Filesystem;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Storage;
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
using UserAuthorization = Deluno.Security.UserAuthorization;

namespace Deluno.Tray;

public sealed class DelunoServer(IStartupProgress? progress = null) : IDisposable
{
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private volatile bool _stopRequested;

    /// <summary>Raised when the web host stopped and nobody asked it to (a critical background service gave up, #1080).</summary>
    public event Action? StoppedUnexpectedly;
    public int? ListeningPort { get; private set; }
    public IReadOnlyList<string> ReachableUrls { get; private set; } = Array.Empty<string>();
    public LanAccessStatus? LanAccess { get; private set; }

    /// <summary>The running host's services, for the tray to follow its pushes and read its counts. Null while no host is running.</summary>
    public IServiceProvider? Services => _app?.Services;

    /// <summary>Reads the firewall again, such as after the rule was added or removed from the tray.</summary>
    public async Task RefreshLanAccessAsync(CancellationToken cancellationToken)
    {
        if (Firewall is null)
        {
            return;
        }

        try
        {
            LanAccess = await Firewall.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Leave the last reading; the next restart reads it again.
        }
    }

    /// <summary>Deluno's firewall rule for the port it is listening on (#623).</summary>
    public WindowsFirewallLanAccess? Firewall { get; private set; }

    /// <summary>Optional port override (from --port CLI arg). Takes precedence over AppSettings.Port.</summary>
    public static int? PortOverride { get; set; }

    /// <summary>
    /// Builds and starts the web host. Throws when it cannot; a host left half-started by a failure is stopped and
    /// disposed first, so its databases and port are free for the next try (#1256). The step it is in is reported to
    /// the start-up progress before each step that can block.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await StartCoreAsync(cancellationToken);
        }
        catch
        {
            await DisposeHalfStartedAsync();
            throw;
        }
    }

    private async Task DisposeHalfStartedAsync()
    {
        var app = _app;
        _app = null;
        if (app is null)
        {
            return;
        }

        _stopRequested = true;
        try { await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); } catch { /* the supervisor frees what is left */ }
    }

    /// <summary>
    /// Stops the host and disposes it, within the token: what the supervisor does with a start that hung or failed so
    /// the databases and the port are released. Not a "stopped by itself": the guard is told it was asked for.
    /// </summary>
    public async Task FreeAsync(CancellationToken cancellationToken)
    {
        _stopRequested = true;
        var app = _app;
        _app = null;
        if (app is null)
        {
            return;
        }

        try { await app.StopAsync(cancellationToken); } catch { /* disposing closes Kestrel */ }
        try { await app.DisposeAsync().AsTask().WaitAsync(cancellationToken); } catch (OperationCanceledException) { throw; } catch { /* best effort */ }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        _stopRequested = false;
        progress?.Enter(StartupPhase.Starting, "building the web host");
        if (_app is not null)
        {
            // A host that stopped by itself is still held here; let it go before starting a new one.
            try { await _app.DisposeAsync(); } catch { /* already stopped */ }
            _app = null;
        }

        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var settings = AppSettings.LoadForStart();
        var port = PortOverride ?? settings.Port;

        var builder = TrayLogging.CreateBuilder(new WebApplicationOptions
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

        // Forwarded headers, response compression and rate limiting: the same
        // services Deluno.Host uses (#1176), so a reverse proxy in front of the
        // Windows install is honoured too.
        builder.Services.AddDelunoWebPipeline(builder.Configuration);

        // The tray restarts the host itself (HostRestartSupervisor), so giving up
        // must not leave a failing exit code behind for the tray process.
        builder.Services.AddSingleton(new BackgroundServiceSafetyOptions { SetExitCodeWhenGivingUp = false });
        if (progress is not null)
        {
            // The migrator, the storage bootstrap and the startup gate report each step through this (#1256).
            builder.Services.AddSingleton(progress);
        }

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
        builder.Services.AddSingleton<IComponentRecoveryNotices>(_ => new TrayComponentRecoveryNotices());
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

        // The request pipeline every Deluno host shares (#1176). The Windows app
        // differs only in its port, firewall and data root, which are set above.
        _app.UseDelunoRequestPipeline();

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

        // Ready means this method is done, not only that Kestrel is listening: the supervisor stops watching a start at
        // Ready, and the firewall reading above is still part of the start. It is still covered by the web host window.
        progress?.Enter(StartupPhase.Ready);
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
