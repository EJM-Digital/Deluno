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
        var log = new TrayStartLog();

        // First of all, on its own thread (#1256): services installed by earlier builds have no restart-on-failure
        // actions, and a first start that hangs must still leave them set. Its answer is kept for the Health row.
        var failureActions = ServiceManager.EnsureFailureActionsEarlyAsync(message => log.Warning(message));

        var settings = AppSettings.Load();
        // So that a Setup over a running service starts it again afterwards (#1256).
        Program.MarkRunning(RunningKind.Service);
        var progress = StartProgress.ForThisProcess();
        progress.BeginAttempt(1);
        using var watchStop = new CancellationTokenSource();
        // The service manager calls the service running as soon as the host's lifetime answers, before the databases
        // are opened, so a hung start is invisible to it. Watch it here; a hang ends the process non-zero so the
        // recovery actions act.
        _ = ServiceStartGuard.WatchAsync(
            progress, new StartWindows(), TimeProvider.System, log, StartDiagnostics.Capture, code => Environment.Exit(code), watchStop.Token);

        var builder = TrayLogging.CreateBuilder(new WebApplicationOptions
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
        builder.Services.AddSingleton<Deluno.Infrastructure.Storage.IStartupProgress>(progress);
        // The Health row for a failed restart-on-failure set-up gets a "Try again" (#1080).
        builder.Services.AddSingleton<Deluno.Contracts.IBackgroundProblemRetry>(provider =>
            new ServiceFailureActionsRetry(provider.GetRequiredService<Deluno.Contracts.IBackgroundProblemReporter>()));

        builder.Services.AddDelunoInfrastructure(builder.Configuration);
        builder.Services.AddDelunoWebPipeline(builder.Configuration);
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

        // The recovery settings were set at the top, before anything could hang; once Deluno is up, the Health row
        // reports a refusal (or is cleared) from that answer (#1080, #1256).
        app.Lifetime.ApplicationStarted.Register(() => progress.Enter(Deluno.Infrastructure.Storage.StartupPhase.Ready));
        app.Lifetime.ApplicationStarted.Register(() => _ = ReportFailureActionsAsync(failureActions, app));

        // The request pipeline every Deluno host shares (#1176). The service ran
        // its own copy with no routing, no allowed hosts and no forwarded headers.
        app.UseDelunoRequestPipeline();

        try
        {
            await app.RunAsync();
        }
        catch (Exception failure)
        {
            // A start that fails outright ends the process non-zero, with the reason in the log, so the service
            // manager's recovery acts (#1256).
            ServiceStartGuard.OnStartFailed(failure, log, code => Environment.Exit(code));
        }
        finally
        {
            watchStop.Cancel();
        }
    }

    private static async Task ReportFailureActionsAsync(Task<string?> failureActions, WebApplication app)
    {
        try
        {
            var problem = await failureActions;
            var reporter = app.Services.GetService<Deluno.Contracts.IBackgroundProblemReporter>();
            if (problem is null)
            {
                reporter?.Clear(ServiceManager.FailureActionsProblemKey);
            }
            else
            {
                reporter?.Report(ServiceManager.FailureActionsProblemKey, "Deluno may not restart itself", ServiceManager.FailureActionsProblemMessage, problem);
            }
        }
        catch
        {
            // Best effort: the log already has it.
        }
    }
}
