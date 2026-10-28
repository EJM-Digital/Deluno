using Deluno.Api;
using Deluno.Api.Updates;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Tray;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1376: an in-app update on Windows must leave the same two records the Docker path does: the applied version on the
/// Updates card (with its date) and one "Deluno updated from X to Y" line in Activity. The old build hands over a note
/// before the installer runs, the install's after-update hook leaves one too (for an old build that did not), and the
/// new build's tray orchestrator reads it once. The installed half (a real feed, a real download, a real restart) is the
/// <c>update.in-app</c> golden-path scenario.
/// </summary>
public sealed class AnUpdateAppliedOnWindowsIsRecordedTests : IDisposable
{
    private readonly string? _originalLocalAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA");
    private readonly string? _originalCommonAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-update-applied-" + Guid.NewGuid().ToString("N"));

    public AnUpdateAppliedOnWindowsIsRecordedTests()
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", Path.Combine(_root, "local"));
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", Path.Combine(_root, "common"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", _originalLocalAppData);
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", _originalCommonAppData);
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private string NotePath => Path.Combine(_root, "local", "DelunoConfig", "update-applied.json");

    [Fact]
    public void The_note_the_old_build_leaves_is_read_once_with_both_versions_and_then_gone()
    {
        UpdateAppliedNote.WriteBeforeApply("0.30.33", "0.30.35");
        var source = new TrayAppliedUpdateHintSource();

        var hint = source.Peek();
        Assert.Equal(new AppliedUpdateHint("0.30.33", "0.30.35"), hint);

        source.Consume();
        Assert.Null(source.Peek());
        Assert.False(File.Exists(NotePath));
    }

    [Fact]
    public void The_after_update_hook_keeps_a_note_that_already_says_where_the_update_came_from()
    {
        UpdateAppliedNote.WriteBeforeApply("0.30.33", "0.30.35");
        UpdateAppliedNote.WriteAfterUpdate("0.30.35");
        Assert.Equal("0.30.33", new TrayAppliedUpdateHintSource().Peek()!.FromVersion);

        // A note for some other version is replaced.
        UpdateAppliedNote.WriteAfterUpdate("0.31.0");
        Assert.Equal("0.31.0", new TrayAppliedUpdateHintSource().Peek()!.ToVersion);
    }

    [Fact]
    public void The_update_hook_leaves_the_note_from_its_own_arguments()
    {
        // The golden VM's in-app update from 1.0.0-rc.30.35 (28 Oct): Update.exe ran the new build with
        // "--veloapp-updated 1.0.0-rc.30.36", the fast hook answered before Velopack was loaded, and no note was left.
        UpdateAppliedNote.WriteFromHookArguments(["--veloapp-updated", "1.0.0-rc.30.36"]);

        Assert.Equal("1.0.0-rc.30.36", new TrayAppliedUpdateHintSource().Peek()!.ToVersion);
    }

    [Fact]
    public void A_first_install_hook_leaves_no_note()
    {
        UpdateAppliedNote.WriteFromHookArguments(["--veloapp-install", "1.0.0-rc.30.36"]);

        Assert.False(File.Exists(NotePath));
    }

    // The nuspec in Deluno-1.0.0-rc.30.36-rc-full.nupkg as the release shipped it, with only the version and channel filled in.
    private const string ShippedNuspec = """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
        <metadata>
        <id>Deluno</id>
        <title>Deluno</title>
        <description>Deluno</description>
        <authors>Deluno</authors>
        <version>{0}</version>
        <channel>{1}</channel>
        <mainExe>Deluno.exe</mainExe>
        <os>win</os>
        <rid>win-x64</rid>
        <shortcutLocations>Desktop,StartMenuRoot</shortcutLocations>
        <shortcutAmuid>velopack.Deluno</shortcutAmuid>
        <machineArchitecture>x64</machineArchitecture>
        </metadata>
        </package>
        """;

    private static void Package(string folder, string fileName, string version, string channel)
    {
        using var zip = System.IO.Compression.ZipFile.Open(Path.Combine(folder, fileName), System.IO.Compression.ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("Deluno.nuspec").Open());
        writer.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture, ShippedNuspec, version, channel));
    }

    [Theory]
    // As shipped: the channel is in the name, "-rc-full". The rc.30.36 suite's update.in-app named "1.0.0-rc.30.36-rc" as the
    // version it came from (#1387).
    [InlineData("rc", "Deluno-{0}-rc-full.nupkg")]
    [InlineData("win", "Deluno-{0}-win-full.nupkg")]
    [InlineData("stable", "Deluno-{0}-full.nupkg")]
    public void The_previous_version_is_the_newest_other_full_package_Velopack_kept(string channel, string nameFormat)
    {
        var packages = Path.Combine(_root, "packages");
        Directory.CreateDirectory(packages);
        foreach (var version in new[] { "1.0.0-rc.30.33", "1.0.0-rc.30.35", "1.0.0-rc.30.36" })
        {
            Package(packages, string.Format(System.Globalization.CultureInfo.InvariantCulture, nameFormat, version), version, channel);
        }
        File.WriteAllText(Path.Combine(packages, "Deluno-1.0.0-rc.30.36-rc-delta.nupkg"), "not a full package");
        File.WriteAllText(Path.Combine(packages, "Deluno-9.9.9-rc-full.nupkg"), "a damaged package names no version");

        Assert.Equal("1.0.0-rc.30.35", UpdateAppliedNote.PreviousVersionFromPackages(packages, "1.0.0-rc.30.36"));
        Assert.Null(UpdateAppliedNote.PreviousVersionFromPackages(Path.Combine(_root, "nowhere"), "1.0.0-rc.30.36"));
    }

    [Fact]
    public async Task The_updates_card_lists_the_applied_version_and_activity_holds_one_line_after_an_update_with_no_earlier_history()
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        UpdateAppliedNote.WriteBeforeApply("0.30.33", DelunoVersion.Running);
        var activity = new ActivityLines();
        var recorder = new AppliedUpdateRecorder(
            Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            new NoPreferences(),
            new NoOutbound(),
            activityFeed: activity,
            hints: new TrayAppliedUpdateHintSource());
        var orchestrator = new VelopackUpdateOrchestrator(NullLogger<VelopackUpdateOrchestrator>.Instance, recorder);

        var status = await orchestrator.GetStatusAsync(CancellationToken.None);
        await orchestrator.GetStatusAsync(CancellationToken.None);

        var applied = Assert.Single(status.AppliedUpdates ?? []);
        Assert.Equal(DelunoVersion.Running, applied.Version);
        Assert.True(applied.AppliedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal($"Deluno updated from 0.30.33 to {DelunoVersion.Running}.", Assert.Single(activity.Lines));
        Assert.False(File.Exists(NotePath));
    }

    [Fact]
    public async Task A_start_that_follows_no_update_records_nothing()
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        var activity = new ActivityLines();
        var recorder = new AppliedUpdateRecorder(
            Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            new NoPreferences(),
            new NoOutbound(),
            activityFeed: activity,
            hints: new TrayAppliedUpdateHintSource());
        var orchestrator = new VelopackUpdateOrchestrator(NullLogger<VelopackUpdateOrchestrator>.Instance, recorder);

        var status = await orchestrator.GetStatusAsync(CancellationToken.None);

        Assert.Empty(status.AppliedUpdates ?? []);
        Assert.Empty(activity.Lines);
    }

    private sealed class NoPreferences : INotificationService
    {
        public Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new NotificationPreferences());

        public Task UpdatePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOutbound : IOutboundNotificationService
    {
        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null) => Task.CompletedTask;

        public Task<NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class ActivityLines : IActivityFeedRepository
    {
        public List<string> Lines { get; } = [];

        public Task<ActivityEventItem> RecordActivityAsync(string category, string message, string? detailsJson, string? relatedJobId, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
        {
            Lines.Add(message);
            return Task.FromResult<ActivityEventItem>(null!);
        }

        public Task<IReadOnlyList<ActivityEventItem>> ListActivityAsync(int take, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Page<ActivityEventItem>> ListActivityPageAsync(PageRequest request, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ActivityEventItem>> RecordActivityBatchAsync(IReadOnlyList<ActivityEventItem> activities, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> ClearActivityAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ActivityStatsSnapshot> GetActivityStatsAsync(DateTimeOffset sinceUtc, int windowHours, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
