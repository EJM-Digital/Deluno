using System.Reflection;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Microsoft.Extensions.Options;

namespace Deluno.Api.Updates;

public sealed class DefaultUpdateOrchestrator : IUpdateOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private const int MaxHistoryEntries = 5;

    private readonly string _installKind;
    private readonly string _currentVersion;
    private readonly string _preferencesPath;
    private readonly string _historyPath;
    private readonly string? _currentImageRef;
    private readonly string? _currentImageDigest;
    private readonly SemaphoreSlim _preferencesGate = new(1, 1);
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private readonly INotificationService _notificationService;
    private readonly IOutboundNotificationService _outboundNotificationService;
    private readonly IReleaseNotesProvider _releaseNotesProvider;
    private readonly TimeProvider _timeProvider;
    private readonly IActivityFeedRepository? _activityFeed;
    private readonly IRealtimeEventPublisher? _realtime;
    private bool _versionCheckedThisRun;

    public DefaultUpdateOrchestrator(
        IOptions<StoragePathOptions> storageOptions,
        INotificationService notificationService,
        IOutboundNotificationService outboundNotificationService,
        IReleaseNotesProvider? releaseNotesProvider = null,
        TimeProvider? timeProvider = null,
        IActivityFeedRepository? activityFeed = null,
        IRealtimeEventPublisher? realtime = null)
    {
        _activityFeed = activityFeed;
        _realtime = realtime;
        _currentVersion = DelunoVersion.Running;
        _installKind = IsRunningInDocker() ? UpdateInstallKinds.Docker : UpdateInstallKinds.Manual;
        var dataRoot = Path.GetFullPath(storageOptions.Value.DataRoot);
        _preferencesPath = Path.Combine(dataRoot, "update-preferences.json");
        _historyPath = Path.Combine(dataRoot, "update-history.json");
        _currentImageRef = Environment.GetEnvironmentVariable("DELUNO_IMAGE_REF");
        _currentImageDigest = Environment.GetEnvironmentVariable("DELUNO_IMAGE_DIGEST")
            ?? ExtractImageDigest(_currentImageRef);
        _notificationService = notificationService;
        _outboundNotificationService = outboundNotificationService;
        _releaseNotesProvider = releaseNotesProvider ?? new EmbeddedReleaseNotesProvider();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<UpdateStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var history = await DetectAppliedUpdateOnceAsync(cancellationToken);
        return BuildStatus(history);
    }

    public async Task<UpdateStatusResponse> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        var history = await DetectAppliedUpdateOnceAsync(cancellationToken);
        await _realtime.PublishUpdateStatusChangedAsync(cancellationToken);
        return BuildStatus(history);
    }

    public async Task<UpdateStatusResponse> DownloadUpdatesAsync(CancellationToken cancellationToken)
    {
        var history = await DetectAppliedUpdateOnceAsync(cancellationToken);
        await _realtime.PublishUpdateStatusChangedAsync(cancellationToken);
        return BuildStatus(history);
    }

    public async Task<UpdateStatusResponse> ApplyAndRestartNowAsync(CancellationToken cancellationToken)
    {
        var history = await DetectAppliedUpdateOnceAsync(cancellationToken);
        await _realtime.PublishUpdateStatusChangedAsync(cancellationToken);
        return BuildStatus(history);
    }

    public async Task<UpdatePreferencesResponse> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        await _preferencesGate.WaitAsync(cancellationToken);
        try
        {
            return ToResponse(await ReadPreferencesAsync(cancellationToken));
        }
        finally
        {
            _preferencesGate.Release();
        }
    }

    public async Task<UpdatePreferencesResponse> SavePreferencesAsync(UpdatePreferencesRequest request, CancellationToken cancellationToken)
    {
        await _preferencesGate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadPreferencesAsync(cancellationToken);
            var next = new UpdatePreferencesState
            {
                Mode = NormalizeMode(request.Mode ?? current.Mode),
                Channel = NormalizeChannel(request.Channel ?? current.Channel),
                AutoCheck = request.AutoCheck ?? current.AutoCheck
            };
            await WritePreferencesAsync(next, cancellationToken);
            await _realtime.PublishUpdateStatusChangedAsync(cancellationToken);
            return ToResponse(next);
        }
        finally
        {
            _preferencesGate.Release();
        }
    }

    private async Task<UpdatePreferencesState> ReadPreferencesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_preferencesPath))
        {
            return UpdatePreferencesState.Default();
        }

        try
        {
            await using var stream = File.OpenRead(_preferencesPath);
            var state = await JsonSerializer.DeserializeAsync<UpdatePreferencesState>(stream, JsonOptions, cancellationToken);
            return state is null
                ? UpdatePreferencesState.Default()
                : new UpdatePreferencesState
                {
                    Mode = NormalizeMode(state.Mode),
                    Channel = NormalizeChannel(state.Channel),
                    AutoCheck = state.AutoCheck
                };
        }
        catch (JsonException)
        {
            return UpdatePreferencesState.Default();
        }
    }

    private async Task WritePreferencesAsync(UpdatePreferencesState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_preferencesPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _preferencesPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _preferencesPath, overwrite: true);
    }

    private static UpdatePreferencesResponse ToResponse(UpdatePreferencesState state)
        => new(state.Mode, state.Channel, state.AutoCheck);

    private static string NormalizeMode(string? value)
        => UpdateModes.IsValid(value) ? value! : UpdateModes.NotifyOnly;

    private static string NormalizeChannel(string? value)
        => string.IsNullOrWhiteSpace(value) ? "stable" : value.Trim().ToLowerInvariant();

    /// <summary>
    /// Detects a version change since the last recorded run and, on the
    /// first such detection per process, records it to the applied-update
    /// history (#870) and fires the "On Application Update" outbound
    /// notification (#856), gated by NotificationPreferences.UpdateEnabled.
    /// Cheap no-op on every call after the first per process.
    /// </summary>
    private async Task<UpdateHistoryState> DetectAppliedUpdateOnceAsync(CancellationToken cancellationToken)
    {
        if (_versionCheckedThisRun)
        {
            return await ReadHistoryAsync(cancellationToken);
        }

        await _historyGate.WaitAsync(cancellationToken);
        try
        {
            if (_versionCheckedThisRun)
            {
                return await ReadHistoryAsync(cancellationToken);
            }

            var history = await ReadHistoryAsync(cancellationToken);
            _versionCheckedThisRun = true;

            if (string.Equals(history.LastKnownVersion, _currentVersion, StringComparison.Ordinal))
            {
                return history;
            }

            var isFirstRunEver = string.IsNullOrEmpty(history.LastKnownVersion);
            var now = _timeProvider.GetUtcNow();
            history.LastKnownVersion = _currentVersion;

            // A brand-new install has nothing to record as "applied" -- only
            // a version that actually changed between runs is an update.
            if (!isFirstRunEver)
            {
                history.Applied.Insert(0, new AppliedUpdateEntry(_currentVersion, now));
                while (history.Applied.Count > MaxHistoryEntries)
                {
                    history.Applied.RemoveAt(history.Applied.Count - 1);
                }

                await DispatchAppUpdateNotificationAsync(now, cancellationToken);
            }

            await WriteHistoryAsync(history, cancellationToken);
            await _realtime.PublishUpdateStatusChangedAsync(cancellationToken);
            return history;
        }
        finally
        {
            _historyGate.Release();
        }
    }

    private async Task DispatchAppUpdateNotificationAsync(DateTimeOffset appliedAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            // Activity says a new version is running whether or not anyone asked
            // to be told; only the outbound message is opt-in (#1017).
            if (_activityFeed is not null)
            {
                await _activityFeed.RecordActivityAsync(
                    "app.update",
                    $"Deluno updated and is now running version {_currentVersion}.",
                    JsonSerializer.Serialize(new { version = _currentVersion, appliedAtUtc }),
                    null,
                    "system",
                    "app-update",
                    cancellationToken);
            }

            var preferences = await _notificationService.GetPreferencesAsync(cancellationToken);
            if (!preferences.UpdateEnabled)
            {
                return;
            }

            await _outboundNotificationService.DispatchAsync(
                "app.update",
                "Deluno updated",
                $"Deluno is now running version {_currentVersion}.",
                detailsJson: JsonSerializer.Serialize(new { version = _currentVersion, appliedAtUtc }),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Never let a notification failure block reporting update status.
        }
    }

    private async Task<UpdateHistoryState> ReadHistoryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_historyPath))
        {
            return new UpdateHistoryState();
        }

        try
        {
            await using var stream = File.OpenRead(_historyPath);
            var state = await JsonSerializer.DeserializeAsync<UpdateHistoryState>(stream, JsonOptions, cancellationToken);
            return state ?? new UpdateHistoryState();
        }
        catch (JsonException)
        {
            return new UpdateHistoryState();
        }
    }

    private async Task WriteHistoryAsync(UpdateHistoryState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_historyPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _historyPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _historyPath, overwrite: true);
    }

    private UpdateStatusResponse BuildStatus(UpdateHistoryState? history = null)
    {
        // Notes are looked up fresh here rather than stored on the entries in
        // update-history.json, so a later edit to RELEASE_NOTES.md (a typo
        // fix, for example) shows up immediately without rewriting history
        // already on disk (#870).
        var appliedWithNotes = history?.Applied
            .Select(entry => entry with { Notes = _releaseNotesProvider.GetNotes(entry.Version) })
            .ToList();

        var message = _installKind == UpdateInstallKinds.Docker
            ? "Docker installs do not support in-place binary updates. Pull a newer image tag and recreate the container."
            : "This runtime is not a Velopack-managed Windows install. Update by installing a newer build package.";

        var notes = _installKind == UpdateInstallKinds.Docker
            ? new[]
            {
                "Update: set DELUNO_IMAGE to the chosen version or digest, then run `docker compose pull deluno`.",
                "Recreate: run `docker compose up -d --no-build` and wait for `/api/health/ready` to return HTTP 200 while migrations finish.",
                "Verify: use `docker image inspect` to record the resolved image digest before declaring the rollout complete.",
                "Rollback: pin DELUNO_IMAGE to the previous digest and recreate with the same `/data` volume; restore a pre-upgrade backup if the schema is not backward-compatible."
            }
            : new[]
            {
                "In-app apply controls are only enabled for Velopack-managed Windows installs.",
                "Keep Storage__DataRoot outside the app folder."
            };

        return new UpdateStatusResponse(
            CurrentVersion: _currentVersion,
            Channel: "stable",
            InstallKind: _installKind,
            BehaviorMode: UpdateModes.NotifyOnly,
            IsInstalled: false,
            CanCheck: false,
            CanDownload: false,
            CanApply: false,
            UpdateAvailable: false,
            LatestVersion: null,
            State: UpdateStates.NotSupported,
            ProgressPercent: null,
            RestartRequired: false,
            LastCheckedUtc: null,
            LastDownloadedUtc: null,
            Message: message,
            LastError: null,
            Notes: notes,
            CurrentImageRef: _currentImageRef,
            CurrentImageDigest: _currentImageDigest,
            // See the Changelog comment on UpdateStatusResponse: there is no
            // update checker, so LatestVersion is always null and this stays
            // null with it.
            Changelog: null,
            AppliedUpdates: appliedWithNotes ?? []);
    }

    private static bool IsRunningInDocker()
    {
        var envFlag = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        if (bool.TryParse(envFlag, out var runningInContainer) && runningInContainer)
        {
            return true;
        }

        var aspNetEnvFlag = Environment.GetEnvironmentVariable("ASPNETCORE_RUNNING_IN_CONTAINER");
        if (bool.TryParse(aspNetEnvFlag, out runningInContainer) && runningInContainer)
        {
            return true;
        }

        return false;
    }

    private static string? ExtractImageDigest(string? imageRef)
    {
        if (string.IsNullOrWhiteSpace(imageRef))
        {
            return null;
        }

        var separator = imageRef.LastIndexOf("@sha256:", StringComparison.OrdinalIgnoreCase);
        return separator >= 0
            ? imageRef[(separator + 1)..]
            : null;
    }

    private sealed class UpdatePreferencesState
    {
        public string Mode { get; set; } = UpdateModes.NotifyOnly;

        public string Channel { get; set; } = "stable";

        public bool AutoCheck { get; set; }

        public static UpdatePreferencesState Default() => new();
    }

    private sealed class UpdateHistoryState
    {
        public string? LastKnownVersion { get; set; }

        public List<AppliedUpdateEntry> Applied { get; set; } = [];
    }
}
