using System.Reflection;
using Deluno.Api.Updates;
using Velopack;
using Velopack.Sources;

namespace Deluno.Tray;

public sealed class VelopackUpdateOrchestrator(ILogger<VelopackUpdateOrchestrator> logger) : IUpdateOrchestrator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
    private readonly UpdateStateMachine _runtime = new();
    private UpdateManager? _manager;
    private string _managerKey = string.Empty;
    private UpdateInfo? _availableUpdate;

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

            var update = await manager.CheckForUpdatesAsync();
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
            return await GetStatusUnlockedAsync(cancellationToken, "Update check failed.");
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
                var update = await manager.CheckForUpdatesAsync();
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

    public async Task<UpdateStatusResponse> PrepareApplyOnNextRestartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = AppSettings.Load();
            var manager = GetOrCreateManager(settings);
            if (!manager.IsInstalled)
            {
                return BuildStatus(settings, manager, "Apply controls require a Velopack-installed app.");
            }

            if (manager.UpdatePendingRestart is null)
            {
                if (_availableUpdate is null)
                {
                    _availableUpdate = await manager.CheckForUpdatesAsync();
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

            var restartRequired = manager.UpdatePendingRestart is not null || _availableUpdate is not null;
            if (restartRequired)
            {
                _runtime.MarkReadyToRestart();
            }
            else
            {
                _runtime.MarkUpToDate(_runtime.LastCheckedUtc ?? DateTimeOffset.UtcNow, restartRequired: false);
            }
            return BuildStatus(
                settings,
                manager,
                restartRequired
                    ? "Update is staged and will apply on restart."
                    : "No update is ready to apply.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Apply-on-restart preparation failed.");
            _runtime.MarkError(ex.Message);
            return await GetStatusUnlockedAsync(cancellationToken, "Could not prepare the update.");
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
                    _availableUpdate = await manager.CheckForUpdatesAsync();
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
        IUpdateSource source = sourceUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            ? new GithubSource(sourceUrl, accessToken: null, prerelease: false)
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
            "Updates are checked against the stable channel by default.",
            "Deluno data lives outside the app install folder so updates do not overwrite your databases or keys.",
            "A backup is created before restart-based apply from the System > Updates page."
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
