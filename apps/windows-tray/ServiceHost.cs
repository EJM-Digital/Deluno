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

        // Services installed by earlier builds have no restart-on-failure actions; set
        // them again on every start (sc failure is idempotent). Off the start path, once
        // Deluno is up, so three slow sc.exe calls can never eat the service manager's
        // 30 seconds to start; a refusal is logged and shown on Health (#1080).
        app.Lifetime.ApplicationStarted.Register(() => _ = ServiceManager.EnsureFailureActionsInBackground(
            message => app.Logger.LogWarning("{Message}", message),
            app.Services.GetService<Deluno.Contracts.IBackgroundProblemReporter>()));

        // The request pipeline every Deluno host shares (#1176). The service ran
        // its own copy with no routing, no allowed hosts and no forwarded headers.
        app.UseDelunoRequestPipeline();

        await app.RunAsync();
    }
}
