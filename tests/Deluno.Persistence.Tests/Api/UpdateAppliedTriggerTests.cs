using System.Text.Json;
using Deluno.Api.Updates;
using Deluno.Infrastructure.Storage;
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
}
