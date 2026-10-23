using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Microsoft.Extensions.Options;

namespace Deluno.Api.Updates;

/// <summary>
/// What an installer that knows an update was just applied tells the server (#1376). A Windows update restarts Deluno
/// as the new build; a build older than the history file never recorded the version it was running, so the server
/// cannot work the update out for itself and has to be told.
/// </summary>
public sealed record AppliedUpdateHint(string? FromVersion, string ToVersion);

/// <summary>Where a hint comes from. The default says nothing; the Windows tray reads the note its update hook leaves.</summary>
public interface IAppliedUpdateHintSource
{
    AppliedUpdateHint? Peek();

    /// <summary>Called once the update was recorded, so the same hint is never counted twice.</summary>
    void Consume();
}

public sealed class NoAppliedUpdateHint : IAppliedUpdateHintSource
{
    public AppliedUpdateHint? Peek() => null;

    public void Consume()
    {
    }
}

/// <summary>
/// Records "Deluno updated" once per version change: the applied-update history (#870), the Activity line (#1017) and the
/// opt-in outbound notification (#856). Shared by the Docker/manual orchestrator and the Windows tray one, so both
/// kinds of install record the same things the same way (#1376).
/// </summary>
public sealed class AppliedUpdateRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private const int MaxHistoryEntries = 5;

    private readonly string _historyPath;
    private readonly string _currentVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly INotificationService _notificationService;
    private readonly IOutboundNotificationService _outboundNotificationService;
    private readonly IReleaseNotesProvider _releaseNotesProvider;
    private readonly TimeProvider _timeProvider;
    private readonly IActivityFeedRepository? _activityFeed;
    private readonly IRealtimeEventPublisher? _realtime;
    private readonly IAppliedUpdateHintSource _hints;
    private bool _checkedThisRun;

    public AppliedUpdateRecorder(
        IOptions<StoragePathOptions> storageOptions,
        INotificationService notificationService,
        IOutboundNotificationService outboundNotificationService,
        IReleaseNotesProvider? releaseNotesProvider = null,
        TimeProvider? timeProvider = null,
        IActivityFeedRepository? activityFeed = null,
        IRealtimeEventPublisher? realtime = null,
        IAppliedUpdateHintSource? hints = null)
    {
        _historyPath = Path.Combine(Path.GetFullPath(storageOptions.Value.DataRoot), "update-history.json");
        _currentVersion = DelunoVersion.Running;
        _notificationService = notificationService;
        _outboundNotificationService = outboundNotificationService;
        _releaseNotesProvider = releaseNotesProvider ?? new EmbeddedReleaseNotesProvider();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _activityFeed = activityFeed;
        _realtime = realtime;
        _hints = hints ?? new NoAppliedUpdateHint();
    }

    /// <summary>
    /// Compares the running version with the one last recorded and, on the first call per process, records an update if
    /// it changed (or the installer says one was applied). Cheap after the first call. Returns the applied versions,
    /// newest first, each with its release notes.
    /// </summary>
    public async Task<IReadOnlyList<AppliedUpdateEntry>> DetectAsync(CancellationToken cancellationToken)
    {
        if (!_checkedThisRun)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!_checkedThisRun)
                {
                    await RecordIfChangedAsync(cancellationToken);
                    _checkedThisRun = true;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        var history = await ReadHistoryAsync(cancellationToken);
        // Notes are looked up fresh rather than stored in update-history.json, so a later edit to RELEASE_NOTES.md shows
        // up without rewriting history already on disk (#870).
        return history.Applied
            .Select(entry => entry with { Notes = _releaseNotesProvider.GetNotes(entry.Version) })
            .ToList();
    }

    private async Task RecordIfChangedAsync(CancellationToken cancellationToken)
    {
        var history = await ReadHistoryAsync(cancellationToken);
        var hint = _hints.Peek();
        var hintIsForThisVersion = hint is not null && DelunoVersion.Same(hint.ToVersion, _currentVersion);
        var known = history.LastKnownVersion;
        var versionChanged = !string.IsNullOrEmpty(known) && !string.Equals(known, _currentVersion, StringComparison.Ordinal);
        var alreadyRecorded = history.Applied.Count > 0
            && string.Equals(history.Applied[0].Version, _currentVersion, StringComparison.Ordinal)
            && string.Equals(known, _currentVersion, StringComparison.Ordinal);

        if (string.Equals(known, _currentVersion, StringComparison.Ordinal) && (!hintIsForThisVersion || alreadyRecorded))
        {
            if (hint is not null) _hints.Consume();
            return;
        }

        // A brand-new install has nothing to record as "applied": only a version that changed between runs, or one the
        // installer says it applied, is an update.
        var isUpdate = versionChanged || hintIsForThisVersion;
        var from = versionChanged ? known : (hintIsForThisVersion ? hint!.FromVersion : null);
        var now = _timeProvider.GetUtcNow();
        history.LastKnownVersion = _currentVersion;

        if (isUpdate)
        {
            history.Applied.Insert(0, new AppliedUpdateEntry(_currentVersion, now));
            while (history.Applied.Count > MaxHistoryEntries)
            {
                history.Applied.RemoveAt(history.Applied.Count - 1);
            }

            await DispatchAsync(from, now, cancellationToken);
        }

        await WriteHistoryAsync(history, cancellationToken);
        if (hint is not null) _hints.Consume();
        if (_realtime is not null)
        {
            await _realtime.PublishUpdateStatusChangedAsync(cancellationToken);
        }
    }

    private async Task DispatchAsync(string? from, DateTimeOffset appliedAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            var text = string.IsNullOrWhiteSpace(from)
                ? $"Deluno updated to version {_currentVersion}."
                : $"Deluno updated from {from} to {_currentVersion}.";
            var details = JsonSerializer.Serialize(new { version = _currentVersion, from, appliedAtUtc });

            // Activity says a new version is running whether or not anyone asked to be told; only the outbound message
            // is opt-in (#1017).
            if (_activityFeed is not null)
            {
                await _activityFeed.RecordActivityAsync("app.update", text, details, null, "system", "app-update", cancellationToken);
            }

            var preferences = await _notificationService.GetPreferencesAsync(cancellationToken);
            if (!preferences.UpdateEnabled)
            {
                return;
            }

            await _outboundNotificationService.DispatchAsync(
                "app.update",
                "Deluno updated",
                text,
                detailsJson: details,
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

    private sealed class UpdateHistoryState
    {
        public string? LastKnownVersion { get; set; }

        public List<AppliedUpdateEntry> Applied { get; set; } = [];
    }
}
