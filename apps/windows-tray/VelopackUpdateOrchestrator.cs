using System.Reflection;
using Deluno.Api.Updates;
using Velopack;
using Velopack.Sources;

namespace Deluno.Tray;

public sealed class VelopackUpdateOrchestrator(ILogger<VelopackUpdateOrchestrator> logger) : IUpdateOrchestrator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
    private UpdateManager? _manager;
    private string _managerKey = string.Empty;
    private UpdateInfo? _availableUpdate;
    private string _state = UpdateStates.Idle;
    private string? _latestVersion;
    private string? _lastError;
    private int? _progressPercent;
    private DateTimeOffset? _lastCheckedUtc;
    private DateTimeOffset? _lastDownloadedUtc;
    private bool _restartRequired;
    private bool _updateAvailable;

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
                _state = UpdateStates.NotSupported;
                return BuildStatus(settings, manager, "Updates are available only for a Velopack-installed app.");
            }

            _lastError = null;
            _state = UpdateStates.Checking;
            _progressPercent = null;

            var update = await manager.CheckForUpdatesAsync();
            _lastCheckedUtc = DateTimeOffset.UtcNow;

            if (update is null)
            {
                _availableUpdate = null;
                _latestVersion = null;
                _updateAvailable = false;
                _restartRequired = manager.UpdatePendingRestart is not null;
                _state = _restartRequired ? UpdateStates.ReadyToRestart : UpdateStates.UpToDate;
                return BuildStatus(
                    settings,
                    manager,
                    _restartRequired
                        ? "An already-downloaded update is waiting for restart."
                        : "You are on the latest version.");
            }

            _availableUpdate = update;
            _latestVersion = update.TargetFullRelease.Version.ToString();
            _updateAvailable = true;
            _state = UpdateStates.UpdateAvailable;

            if (settings.AutoCheckUpdates && settings.UpdateMode != UpdateModes.NotifyOnly)
            {
                await DownloadPendingUpdateAsync(settings, manager, cancellationToken);
                return BuildStatus(settings, manager, "Update was found and downloaded in the background.");
            }

            return BuildStatus(settings, manager, $"Version {_latestVersion} is available.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed.");
            _lastError = ex.Message;
            _state = UpdateStates.Error;
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
                _lastCheckedUtc = DateTimeOffset.UtcNow;
                _availableUpdate = update;
            }

            if (_availableUpdate is null)
            {
                _updateAvailable = false;
                _state = UpdateStates.UpToDate;
                return BuildStatus(settings, manager, "No update is available.");
            }

            await DownloadPendingUpdateAsync(settings, manager, cancellationToken);
            return BuildStatus(settings, manager, "Update downloaded. Restart Deluno to apply it.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update download failed.");
            _lastError = ex.Message;
            _state = UpdateStates.Error;
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
                    _lastCheckedUtc = DateTimeOffset.UtcNow;
                }

                if (_availableUpdate is not null)
                {
                    await DownloadPendingUpdateAsync(settings, manager, cancellationToken);
                }
            }

            _restartRequired = manager.UpdatePendingRestart is not null || _availableUpdate is not null;
            _state = _restartRequired ? UpdateStates.ReadyToRestart : UpdateStates.UpToDate;
            return BuildStatus(
                settings,
                manager,
                _restartRequired
                    ? "Update is staged and will apply on restart."
                    : "No update is ready to apply.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Apply-on-restart preparation failed.");
            _lastError = ex.Message;
            _state = UpdateStates.Error;
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
                    _lastCheckedUtc = DateTimeOffset.UtcNow;
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
                _state = UpdateStates.UpToDate;
                return BuildStatus(settings, manager, "No update is ready to apply.");
            }

            _state = UpdateStates.ReadyToRestart;
            _restartRequired = true;

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
                    _lastError = ex.Message;
                    _state = UpdateStates.Error;
                }
            });

            return BuildStatus(settings, manager, "Restarting to finish update.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Restart apply failed.");
            _lastError = ex.Message;
            _state = UpdateStates.Error;
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

        _state = UpdateStates.Downloading;
        _progressPercent = 0;
        _lastError = null;

        await manager.DownloadUpdatesAsync(_availableUpdate, progress =>
        {
            _progressPercent = progress;
        }, cancellationToken);

        _progressPercent = 100;
        _lastDownloadedUtc = DateTimeOffset.UtcNow;
        _restartRequired = manager.UpdatePendingRestart is not null || _availableUpdate is not null;
        _state = _restartRequired ? UpdateStates.ReadyToRestart : UpdateStates.UpToDate;
        _updateAvailable = _restartRequired;
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
            _state = UpdateStates.NotSupported;
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
            UpdateAvailable: _updateAvailable,
            LatestVersion: _latestVersion,
            State: _state,
            ProgressPercent: _progressPercent,
            RestartRequired: _restartRequired,
            LastCheckedUtc: _lastCheckedUtc,
            LastDownloadedUtc: _lastDownloadedUtc,
            Message: message,
            LastError: _lastError,
            Notes: notes);
    }
}
