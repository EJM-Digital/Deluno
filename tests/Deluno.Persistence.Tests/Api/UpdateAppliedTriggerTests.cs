using Deluno.Api;
using System.Text.Json;
using Deluno.Api.Updates;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The "On Application Update" trigger (#856) and the applied-update history
/// it feeds (#870). Deluno has no real self-update pipeline to watch here
/// (DefaultUpdateOrchestrator reports "not supported" for every install
/// kind), so the decided signal is the one thing that is real: the running
/// version differing from what was last recorded to disk between runs.
/// </summary>
public sealed class UpdateAppliedTriggerTests
{
    private static string CreateDataRoot()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "deluno-update-history", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        return dataRoot;
    }

    [Fact]
    public async Task First_run_ever_records_the_version_but_does_not_treat_it_as_an_applied_update()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var options = Options.Create(new StoragePathOptions { DataRoot = dataRoot });
            var outbound = new FakeOutboundNotificationService();
            var notifications = new FakeNotificationService { Preferences = new() { UpdateEnabled = true } };
            var orchestrator = new DefaultUpdateOrchestrator(options, notifications, outbound);

            var status = await orchestrator.GetStatusAsync(CancellationToken.None);

            Assert.Empty(status.AppliedUpdates ?? []);
            Assert.DoesNotContain("app.update", outbound.DispatchedCategories);
            Assert.True(File.Exists(Path.Combine(dataRoot, "update-history.json")));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_version_change_since_the_last_run_is_recorded_and_fires_the_update_trigger_when_enabled()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dataRoot, "update-history.json"),
                JsonSerializer.Serialize(new { lastKnownVersion = "0.0.1-previous-fixture", applied = Array.Empty<object>() }));

            var options = Options.Create(new StoragePathOptions { DataRoot = dataRoot });
            var outbound = new FakeOutboundNotificationService();
            var notifications = new FakeNotificationService { Preferences = new() { UpdateEnabled = true } };
            var orchestrator = new DefaultUpdateOrchestrator(options, notifications, outbound);

            var status = await orchestrator.GetStatusAsync(CancellationToken.None);

            var applied = Assert.Single(status.AppliedUpdates ?? []);
            Assert.Equal(status.CurrentVersion, applied.Version);
            Assert.Equal("app.update", Assert.Single(outbound.DispatchedCategories));

            // Calling again within the same process must not re-fire.
            await orchestrator.GetStatusAsync(CancellationToken.None);
            Assert.Single(outbound.DispatchedCategories);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task An_applied_update_carries_its_own_release_notes()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dataRoot, "update-history.json"),
                JsonSerializer.Serialize(new { lastKnownVersion = "0.0.1-previous-fixture", applied = Array.Empty<object>() }));

            var options = Options.Create(new StoragePathOptions { DataRoot = dataRoot });
            var outbound = new FakeOutboundNotificationService();
            var notifications = new FakeNotificationService { Preferences = new() { UpdateEnabled = true } };
            var currentVersion = Deluno.Api.DelunoVersion.Running;
            var releaseNotes = new FakeReleaseNotesProvider(new Dictionary<string, string>
            {
                [currentVersion] = "- Made the thing better."
            });
            var orchestrator = new DefaultUpdateOrchestrator(options, notifications, outbound, releaseNotes);

            var status = await orchestrator.GetStatusAsync(CancellationToken.None);

            var applied = Assert.Single(status.AppliedUpdates ?? []);
            Assert.Equal("- Made the thing better.", applied.Notes);

            // The pending-update Changelog has nowhere to point yet: there is
            // no update checker, so LatestVersion is always null (#870).
            Assert.Null(status.Changelog);
            Assert.Null(status.LatestVersion);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task An_applied_version_with_no_notes_section_carries_null_notes()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dataRoot, "update-history.json"),
                JsonSerializer.Serialize(new { lastKnownVersion = "0.0.1-previous-fixture", applied = Array.Empty<object>() }));

            var options = Options.Create(new StoragePathOptions { DataRoot = dataRoot });
            var outbound = new FakeOutboundNotificationService();
            var notifications = new FakeNotificationService { Preferences = new() { UpdateEnabled = true } };
            var releaseNotes = new FakeReleaseNotesProvider(new Dictionary<string, string>());
            var orchestrator = new DefaultUpdateOrchestrator(options, notifications, outbound, releaseNotes);

            var status = await orchestrator.GetStatusAsync(CancellationToken.None);

            var applied = Assert.Single(status.AppliedUpdates ?? []);
            Assert.Null(applied.Notes);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class FakeReleaseNotesProvider(IReadOnlyDictionary<string, string> notesByVersion) : IReleaseNotesProvider
    {
        public string? GetNotes(string version) => notesByVersion.TryGetValue(version, out var notes) ? notes : null;
    }

    [Fact]
    public async Task A_version_change_stays_silent_when_the_toggle_is_off()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dataRoot, "update-history.json"),
                JsonSerializer.Serialize(new { lastKnownVersion = "0.0.1-previous-fixture", applied = Array.Empty<object>() }));

            var options = Options.Create(new StoragePathOptions { DataRoot = dataRoot });
            var outbound = new FakeOutboundNotificationService();
            var notifications = new FakeNotificationService { Preferences = new() { UpdateEnabled = false } };
            var orchestrator = new DefaultUpdateOrchestrator(options, notifications, outbound);

            var status = await orchestrator.GetStatusAsync(CancellationToken.None);

            // The history still records the change; only the outbound fire is gated.
            Assert.Single(status.AppliedUpdates ?? []);
            Assert.Empty(outbound.DispatchedCategories);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// #1017: a new version running is written to Activity in plain words even
    /// when nobody switched the outbound message on.
    /// </summary>
    [Fact]
    public async Task An_applied_update_writes_an_activity_line_even_when_the_outbound_trigger_is_off()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dataRoot, "update-history.json"),
                JsonSerializer.Serialize(new { lastKnownVersion = "0.0.1-previous-fixture", applied = Array.Empty<object>() }));

            var options = Options.Create(new StoragePathOptions { DataRoot = dataRoot });
            var outbound = new FakeOutboundNotificationService();
            var notifications = new FakeNotificationService { Preferences = new() { UpdateEnabled = false } };
            var activity = new RecordingActivityFeed();
            var orchestrator = new DefaultUpdateOrchestrator(options, notifications, outbound, activityFeed: activity);

            await orchestrator.GetStatusAsync(CancellationToken.None);

            var line = Assert.Single(activity.Messages);
            Assert.Equal($"Deluno updated from 0.0.1-previous-fixture to {DelunoVersion.Running}.", line);
            Assert.Empty(outbound.DispatchedCategories);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// #1376: the Windows tray runs a different orchestrator, and a build older than the history file never wrote
    /// the version it was running. The installer's note says an update was just applied, so the history and the
    /// Activity line are written even though there is no earlier history to compare with.
    /// </summary>
    private static AppliedUpdateRecorder Recorder(string dataRoot, RecordingActivityFeed activity, IAppliedUpdateHintSource? hint)
        => new(
            Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            new FakeNotificationService { Preferences = new() { UpdateEnabled = false } },
            new FakeOutboundNotificationService(),
            activityFeed: activity,
            hints: hint);

    [Fact]
    public async Task An_installer_note_records_the_update_and_the_version_it_came_from_when_there_is_no_history()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var activity = new RecordingActivityFeed();
            var hint = new FakeHint(new AppliedUpdateHint("0.0.1-previous-fixture", DelunoVersion.Running));
            var recorder = Recorder(dataRoot, activity, hint);

            var applied = Assert.Single(await recorder.DetectAsync(CancellationToken.None));

            Assert.Equal(DelunoVersion.Running, applied.Version);
            Assert.Equal($"Deluno updated from 0.0.1-previous-fixture to {DelunoVersion.Running}.", Assert.Single(activity.Messages));
            Assert.True(hint.Consumed);

            // A second look in the same run, and a fresh run on the same history, add nothing.
            await recorder.DetectAsync(CancellationToken.None);
            var again = Recorder(dataRoot, activity, new FakeHint(new AppliedUpdateHint("0.0.1-previous-fixture", DelunoVersion.Running)));
            Assert.Single(await again.DetectAsync(CancellationToken.None));
            Assert.Single(activity.Messages);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task An_installer_note_without_the_old_version_still_records_the_update_in_plain_words()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var activity = new RecordingActivityFeed();
            var recorder = Recorder(dataRoot, activity, new FakeHint(new AppliedUpdateHint(null, "v" + DelunoVersion.Running)));

            Assert.Single(await recorder.DetectAsync(CancellationToken.None));
            Assert.Equal($"Deluno updated to version {DelunoVersion.Running}.", Assert.Single(activity.Messages));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_note_for_another_version_or_no_note_on_a_new_install_records_nothing()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var activity = new RecordingActivityFeed();
            var stale = new FakeHint(new AppliedUpdateHint("0.0.1", "9.9.9"));
            Assert.Empty(await Recorder(dataRoot, activity, stale).DetectAsync(CancellationToken.None));
            Assert.True(stale.Consumed);
            Assert.Empty(activity.Messages);

            var otherRoot = CreateDataRoot();
            try
            {
                Assert.Empty(await Recorder(otherRoot, activity, null).DetectAsync(CancellationToken.None));
            }
            finally
            {
                Directory.Delete(otherRoot, recursive: true);
            }

            Assert.Empty(activity.Messages);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class FakeHint(AppliedUpdateHint? hint) : IAppliedUpdateHintSource
    {
        public bool Consumed { get; private set; }

        public AppliedUpdateHint? Peek() => Consumed ? null : hint;

        public void Consume() => Consumed = true;
    }

    private sealed class RecordingActivityFeed : IActivityFeedRepository
    {
        public List<string> Messages { get; } = [];

        public Task<ActivityEventItem> RecordActivityAsync(string category, string message, string? detailsJson, string? relatedJobId, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.FromResult<ActivityEventItem>(null!);
        }

        public Task<IReadOnlyList<ActivityEventItem>> ListActivityAsync(int take, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Page<ActivityEventItem>> ListActivityPageAsync(PageRequest request, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ActivityEventItem>> RecordActivityBatchAsync(IReadOnlyList<ActivityEventItem> activities, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> ClearActivityAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ActivityStatsSnapshot> GetActivityStatsAsync(DateTimeOffset sinceUtc, int windowHours, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
