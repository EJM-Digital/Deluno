using System.Reflection;
using Deluno.Api.Updates;
using Velopack;
using Velopack.Sources;

namespace Deluno.Tray;

/// <summary>Tells a feed that is simply not there from a check that really failed.</summary>
public static class UpdateFeedErrors
{
    /// <summary>What the Updates page says when a check could not be made. When GitHub's limit is the cause it is the one sentence the server says too, with the reset as a UTC stamp the web page shows in the viewer's own time (#1181).</summary>
    public static string CheckFailedMessage(Exception exception)
    {
        if (GithubApiClient.LimitOf(exception) is { } limit)
        {
            return limit.Message;
        }

        return IsNotFound(exception)
            ? "Couldn't check for updates: the update feed isn't reachable."
            : "Couldn't check for updates right now. Try Check now again in a moment.";
    }

    public static bool IsNotFound(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
            {
                return true;
            }

            if (current is System.Net.WebException { Response: System.Net.HttpWebResponse { StatusCode: System.Net.HttpStatusCode.NotFound } })
            {
                return true;
            }

            // Some layers keep only the text: "Response status code does not indicate success: 404 (Not Found)."
            if (current is HttpRequestException or System.Net.WebException or InvalidOperationException
                && (current.Message.Contains("404", StringComparison.Ordinal) || current.Message.Contains("Not Found", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The update facts the tray menu words its update item from.</summary>
public sealed record TrayUpdateSnapshot(bool IsInstalled, string State, string? LatestVersion, bool UpdateAvailable, bool RestartRequired);

public sealed class VelopackUpdateOrchestrator(ILogger<VelopackUpdateOrchestrator> logger) : IUpdateOrchestrator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _currentVersion = Deluno.Api.DelunoVersion.Running;
    private readonly UpdateStateMachine _runtime = new();
    private UpdateManager? _manager;
    private string _managerKey = string.Empty;
    private UpdateInfo? _availableUpdate;
    private readonly object _snapshotLock = new();

    /// <summary>Raised when the update state changes (checking, found, downloading, ready), so the tray icon follows without polling.</summary>
    public event Action? Changed
    {
        add => _runtime.Changed += value;
        remove => _runtime.Changed -= value;
    }

    /// <summary>
    /// Set by the tray: stops the host cleanly before an update restarts the app, so a restart asked for from the
    /// web page ends the same way Restart to update on the tray menu does. Null in a host that is not the tray.
    /// </summary>
    public static Func<Task>? CleanStopBeforeApply { get; set; }

    /// <summary>What the tray menu needs to word its update item.</summary>
    public TrayUpdateSnapshot Snapshot()
    {
        lock (_snapshotLock)
        {
            var manager = GetOrCreateManager(AppSettings.Load());
            return new TrayUpdateSnapshot(manager.IsInstalled, _runtime.State, _runtime.LatestVersion, _runtime.UpdateAvailable, _runtime.RestartRequired);
        }
    }

    /// <summary>
    /// A downloaded update is installed when this app exits (Quit), without restarting it. The "pre-update" backup is
    /// taken first, while the host still runs; if it fails nothing is installed and a note is left for the next start
    /// (#1197). Returns true when the install was queued.
    /// </summary>
    public async Task<bool> ApplyPendingOnExitAsync(IServiceProvider? hostServices)
    {
        UpdateManager manager;
        VelopackAsset pending;
        lock (_snapshotLock)
        {
            manager = GetOrCreateManager(AppSettings.Load());
            if (!manager.IsInstalled || manager.UpdatePendingRestart is not { } found)
            {
                return false;
            }

            pending = found;
        }

        return await PreUpdateBackupGate.InstallOnQuitAsync(
            pending.Version.ToString(),
            BlockedUpdateNote.DefaultFolder(),
            token => PreUpdateBackupGate.TakePreUpdateBackupAsync(hostServices, token),
            () => manager.WaitExitThenApplyUpdates(pending, silent: true, restart: false));
    }

    /// <summary>
    /// At start, before the host runs: an update downloaded earlier is applied now and the app restarts into it, after
    /// the same "pre-update" backup (#1197). Tried once per version, so an update that cannot apply never loops; a
    /// failed backup installs nothing and the app starts normally. Returns true when the app is restarting.
    /// </summary>
    public bool ApplyPendingAtStart(
        string attemptFolder,
        Deluno.Infrastructure.Storage.IStartupProgress? progress = null,
        Action? backupStarting = null)
    {
        UpdateManager manager;
        VelopackAsset pending;
        lock (_snapshotLock)
        {
            manager = GetOrCreateManager(AppSettings.Load());
            if (!manager.IsInstalled || manager.UpdatePendingRestart is not { } found)
            {
                return false;
            }

            pending = found;
        }

        // The tray icon appears before the backup, which can take minutes (#1256).
        backupStarting?.Invoke();
        var log = (Action<string>)(line => HookLog.Write(line, TrayStartLog.FileName));
        return Task.Run(() => PreUpdateBackupGate.InstallAtStartAsync(
            pending.Version.ToString(),
            attemptFolder,
            BlockedUpdateNote.DefaultFolder(),
            token => PreUpdateBackupGate.TakePreUpdateBackupAsync(
                null,
                token,
                step =>
                {
                    // Each file is a step: moving on to the next counts as progress.
                    progress?.Enter(Deluno.Infrastructure.Storage.StartupPhase.PreUpdateBackup, step);
                    log($"Backup: {step}");
                }),
            () => manager.ApplyUpdatesAndRestart(pending),
            progress,
            null,
            log)).GetAwaiter().GetResult();
    }

    public async Task<UpdateStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = AppSettings.Load();
            var manager = GetOrCreateManager(settings);
            return BuildStatus(settings, manager, messageOverride: null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UpdateStatusResponse> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = AppSettings.Load();
            var manager = GetOrCreateManager(settings);
            if (!manager.IsInstalled)
            {
                _runtime.MarkNotSupported();
                return BuildStatus(settings, manager, "Updates are available only for a Velopack-installed app.");
            }

            _runtime.MarkChecking();

            var update = await CheckQuietlyAsync(manager);
            var checkedUtc = DateTimeOffset.UtcNow;

            if (update is null)
            {
                _availableUpdate = null;
                var restartRequired = manager.UpdatePendingRestart is not null;
                _runtime.MarkUpToDate(checkedUtc, restartRequired);
                return BuildStatus(
                    settings,
                    manager,
                    restartRequired
                        ? "An already-downloaded update is waiting for restart."
                        : "You are on the latest version.");
            }

            _availableUpdate = update;
            _runtime.MarkUpdateAvailable(checkedUtc, update.TargetFullRelease.Version.ToString());

            if (settings.AutoCheckUpdates && settings.UpdateMode != UpdateModes.NotifyOnly)
            {
                await DownloadPendingUpdateAsync(settings, manager, cancellationToken);
                return BuildStatus(settings, manager, "Update was found and downloaded in the background.");
            }

            return BuildStatus(settings, manager, $"Version {_runtime.LatestVersion} is available.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed.");
            _runtime.MarkError(ex.Message);
            return await GetStatusUnlockedAsync(cancellationToken, UpdateFeedErrors.CheckFailedMessage(ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UpdateStatusResponse> DownloadUpdatesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = AppSettings.Load();
            var manager = GetOrCreateManager(settings);
            if (!manager.IsInstalled)
            {
                return BuildStatus(settings, manager, "Download controls require a Velopack-installed app.");
            }

            if (_availableUpdate is null)
            {
                var update = await CheckQuietlyAsync(manager);
                if (update is null)
                {
                    _runtime.MarkUpToDate(DateTimeOffset.UtcNow, manager.UpdatePendingRestart is not null);
                }
                else
                {
                    _runtime.MarkUpdateAvailable(DateTimeOffset.UtcNow, update.TargetFullRelease.Version.ToString());
                }
                _availableUpdate = update;
            }

            if (_availableUpdate is null)
            {
                return BuildStatus(settings, manager, "No update is available.");
            }

            await DownloadPendingUpdateAsync(settings, manager, cancellationToken);
            return BuildStatus(settings, manager, "Update downloaded. Restart Deluno to apply it.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update download failed.");
            _runtime.MarkError(ex.Message);
            return await GetStatusUnlockedAsync(cancellationToken, "Download failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UpdateStatusResponse> ApplyAndRestartNowAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = AppSettings.Load();
            var manager = GetOrCreateManager(settings);
            if (!manager.IsInstalled)
            {
                return BuildStatus(settings, manager, "Restart apply is only supported for Velopack installs.");
            }

            if (manager.UpdatePendingRestart is null)
            {
                if (_availableUpdate is null)
                {
                    _availableUpdate = await CheckQuietlyAsync(manager);
                    if (_availableUpdate is null)
                    {
                        _runtime.MarkUpToDate(DateTimeOffset.UtcNow, manager.UpdatePendingRestart is not null);
                    }
                    else
                    {
                        _runtime.MarkUpdateAvailable(DateTimeOffset.UtcNow, _availableUpdate.TargetFullRelease.Version.ToString());
                    }
                }

                if (_availableUpdate is not null)
                {
                    await DownloadPendingUpdateAsync(settings, manager, cancellationToken);
                }
            }

            var toApply = manager.UpdatePendingRestart;
            if (toApply is null && _availableUpdate is not null)
            {
                toApply = _availableUpdate;
            }

            if (toApply is null)
            {
                _runtime.MarkUpToDate(_runtime.LastCheckedUtc ?? DateTimeOffset.UtcNow, manager.UpdatePendingRestart is not null);
                return BuildStatus(settings, manager, "No update is ready to apply.");
            }

            _runtime.MarkReadyToRestart();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(350);
                    if (CleanStopBeforeApply is { } stopHost)
                    {
                        try { await stopHost(); } catch { /* the update still applies */ }
                    }

                    manager.ApplyUpdatesAndRestart(toApply);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to apply updates and restart.");
                    _runtime.MarkError(ex.Message);
                }
            });

            return BuildStatus(settings, manager, "Restarting to finish update.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Restart apply failed.");
            _runtime.MarkError(ex.Message);
            return await GetStatusUnlockedAsync(cancellationToken, "Could not restart for update.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UpdatePreferencesResponse> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = AppSettings.Load();
            return new UpdatePreferencesResponse(
                Mode: settings.UpdateMode,
                Channel: settings.UpdateChannel,
                AutoCheck: settings.AutoCheckUpdates);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UpdatePreferencesResponse> SavePreferencesAsync(UpdatePreferencesRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = AppSettings.Load();

            if (!string.IsNullOrWhiteSpace(request.Mode) && UpdateModes.IsValid(request.Mode))
            {
                settings.UpdateMode = request.Mode;
            }

            if (!string.IsNullOrWhiteSpace(request.Channel))
            {
                settings.UpdateChannel = request.Channel.Trim().ToLowerInvariant();
            }

            if (request.AutoCheck.HasValue)
            {
                settings.AutoCheckUpdates = request.AutoCheck.Value;
            }

            settings.Save();

            // Force manager refresh if channel changed.
            _manager = null;
            _managerKey = string.Empty;

            return new UpdatePreferencesResponse(
                Mode: settings.UpdateMode,
                Channel: settings.UpdateChannel,
                AutoCheck: settings.AutoCheckUpdates);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadPendingUpdateAsync(AppSettings settings, UpdateManager manager, CancellationToken cancellationToken)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        _runtime.MarkDownloading();

        await manager.DownloadUpdatesAsync(_availableUpdate, progress =>
        {
            _runtime.ReportProgress(progress);
        }, cancellationToken);

        var restartRequired = manager.UpdatePendingRestart is not null || _availableUpdate is not null;
        _runtime.MarkDownloaded(DateTimeOffset.UtcNow, restartRequired);
    }

    /// <summary>
    /// Asks the feed for an update. A feed that answers 404 (a private or missing repository) is a check that could not
    /// be made, never "up to date": the caller reports it as such (#1181).
    /// </summary>
    private Task<UpdateInfo?> CheckQuietlyAsync(UpdateManager manager) => manager.CheckForUpdatesAsync();

    private UpdateManager GetOrCreateManager(AppSettings settings)
    {
        var key = $"{settings.UpdateSource}|{settings.UpdateChannel}";
        if (_manager is not null && string.Equals(key, _managerKey, StringComparison.Ordinal))
        {
            return _manager;
        }

        var options = new UpdateOptions
        {
            ExplicitChannel = settings.UpdateChannel
        };

        var sourceUrl = settings.UpdateSource;
        // RC channels are represented by prerelease GitHub releases. Stable
        // installs stay on the published stable feed; other channels opt in to
        // prereleases so a freshly-installed RC can find its own channel.
        var includePrereleases = !string.Equals(settings.UpdateChannel, "stable", StringComparison.OrdinalIgnoreCase);
        IUpdateSource source = sourceUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            ? new ReleaseFeedUpdateSource(sourceUrl, includePrereleases)
            : new SimpleWebSource(sourceUrl);

        _manager = new UpdateManager(source, options);
        _managerKey = key;
        return _manager;
    }

    private async Task<UpdateStatusResponse> GetStatusUnlockedAsync(CancellationToken cancellationToken, string fallbackMessage)
    {
        var settings = AppSettings.Load();
        var manager = GetOrCreateManager(settings);
        await Task.CompletedTask;
        return BuildStatus(settings, manager, fallbackMessage);
    }

    private UpdateStatusResponse BuildStatus(AppSettings settings, UpdateManager manager, string? messageOverride)
    {
        var installKind = manager.IsInstalled ? UpdateInstallKinds.WindowsPackaged : UpdateInstallKinds.Manual;
        var message = messageOverride;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = installKind == UpdateInstallKinds.WindowsPackaged
                ? "Windows packaged install is ready for Velopack updates."
                : "This runtime is not installed through Velopack. Use a packaged installer to enable in-app updates.";
        }

        var canOperate = manager.IsInstalled;
        var settingsPathState = AppSettings.InspectPathState();
        var notes = new List<string>
        {
            $"Updates are checked against the {settings.UpdateChannel} channel for this install.",
            "Deluno data lives outside the app install folder so updates do not overwrite your databases or keys.",
            "A backup is taken before every install: Restart and apply, and the install when Deluno quits or starts."
        };

        if (!canOperate)
        {
            _runtime.MarkNotSupported();
            notes.Add("Use the latest Windows installer package to move onto the supported updater path.");
            notes.Add("Manual installs can keep their data root; the packaged installer only replaces app binaries.");
        }

        if (settingsPathState.LegacyConfigExists && settingsPathState.PrimaryConfigExists)
        {
            notes.Add("Legacy settings were detected and migrated to the current config path.");
        }
        else if (settingsPathState.LegacyConfigExists)
        {
            notes.Add("Legacy settings path detected. Deluno will migrate it to the current config path automatically.");
        }

        return new UpdateStatusResponse(
            CurrentVersion: _currentVersion,
            Channel: settings.UpdateChannel,
            InstallKind: installKind,
            BehaviorMode: settings.UpdateMode,
            IsInstalled: canOperate,
            CanCheck: canOperate,
            CanDownload: canOperate,
            CanApply: canOperate,
            UpdateAvailable: _runtime.UpdateAvailable,
            LatestVersion: _runtime.LatestVersion,
            State: _runtime.State,
            ProgressPercent: _runtime.ProgressPercent,
            RestartRequired: _runtime.RestartRequired,
            LastCheckedUtc: _runtime.LastCheckedUtc,
            LastDownloadedUtc: _runtime.LastDownloadedUtc,
            Message: message,
            LastError: _runtime.LastError,
            Notes: notes);
    }
}
