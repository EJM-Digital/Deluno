using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search.Trackers;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #756: fetches tracker definitions from a remote source and writes any
/// that changed into the data folder's trackers directory, the same override
/// TrackerDefinitionCatalog already reads -- and never touches anything while
/// the owner's switch is off, except for the explicit "Update now" call.
/// </summary>
public sealed class TrackerDefinitionUpdateServiceTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "deluno-tracker-update-tests-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private (TrackerDefinitionUpdateService Service, InMemoryStore Store, RecordingActivityFeedRepository Activity, FakeSource Source, TrackerDefinitionCatalog Catalog, FakeClock Clock) Build()
    {
        var trackersDir = Path.Combine(dataRoot, "trackers");
        var options = Options.Create(new StoragePathOptions { DataRoot = dataRoot });
        var catalog = new TrackerDefinitionCatalog(trackersDir, NullLogger<TrackerDefinitionCatalog>.Instance);
        var store = new InMemoryStore();
        var activity = new RecordingActivityFeedRepository();
        var source = new FakeSource();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var service = new TrackerDefinitionUpdateService(
            store, source, catalog, options, activity, clock, NullLogger<TrackerDefinitionUpdateService>.Instance);
        return (service, store, activity, source, catalog, clock);
    }

    [Fact]
    public async Task Update_now_writes_a_new_definition_reloads_the_catalog_and_logs_activity()
    {
        var (service, _, activity, source, catalog, _) = Build();
        source.Definitions.Add(new RemoteTrackerDefinition("newtracker", ValidYaml("newtracker")));

        Assert.Null(catalog.Find("newtracker"));

        var state = await service.UpdateNowAsync(CancellationToken.None);

        Assert.Equal(TrackerDefinitionUpdateStatuses.Updated, state.Status);
        Assert.Equal(["newtracker"], state.UpdatedTrackerIds);
        Assert.NotNull(state.LastUpdatedUtc);
        Assert.NotNull(catalog.Find("newtracker"));
        var recorded = Assert.Single(activity.Recorded);
        Assert.Equal("trackers.definitions.update", recorded.Category);
        Assert.Contains("newtracker", recorded.Message);
    }

    [Fact]
    public async Task Update_now_runs_even_when_the_scheduled_switch_is_off()
    {
        var (service, store, _, source, _, _) = Build();
        await store.SetSettingsAsync(isEnabled: false, intervalHours: 24, CancellationToken.None);
        source.Definitions.Add(new RemoteTrackerDefinition("newtracker", ValidYaml("newtracker")));

        var state = await service.UpdateNowAsync(CancellationToken.None);

        Assert.Equal(["newtracker"], state.UpdatedTrackerIds);
    }

    [Fact]
    public async Task Run_if_due_does_nothing_while_the_scheduled_switch_is_off()
    {
        var (service, store, activity, source, catalog, _) = Build();
        await store.SetSettingsAsync(isEnabled: false, intervalHours: 24, CancellationToken.None);
        source.Definitions.Add(new RemoteTrackerDefinition("newtracker", ValidYaml("newtracker")));

        var state = await service.RunIfDueAsync(CancellationToken.None);

        Assert.Equal(TrackerDefinitionUpdateStatuses.Disabled, state.Status);
        Assert.Empty(activity.Recorded);
        Assert.Null(catalog.Find("newtracker"));
        Assert.False(source.WasCalled);
    }

    [Fact]
    public async Task A_changed_existing_definition_is_named_as_updated_not_added()
    {
        var (service, store, activity, source, catalog, _) = Build();
        await store.SetSettingsAsync(isEnabled: true, intervalHours: 24, CancellationToken.None);
        var trackersDir = Path.Combine(dataRoot, "trackers");
        Directory.CreateDirectory(trackersDir);
        await File.WriteAllTextAsync(Path.Combine(trackersDir, "existingtracker.yml"), ValidYaml("existingtracker"));
        catalog.Reload();
        source.Definitions.Add(new RemoteTrackerDefinition("existingtracker", ValidYaml("existingtracker") + "\n# a fixed selector\n"));

        var state = await service.UpdateNowAsync(CancellationToken.None);

        Assert.Equal(["existingtracker"], state.UpdatedTrackerIds);
        var recorded = Assert.Single(activity.Recorded);
        Assert.Contains("updated the existingtracker tracker definition", recorded.Message);
        Assert.DoesNotContain("added", recorded.Message);
    }

    [Fact]
    public async Task A_pass_with_both_a_new_and_a_changed_definition_names_each_apart()
    {
        var (service, store, activity, source, catalog, _) = Build();
        await store.SetSettingsAsync(isEnabled: true, intervalHours: 24, CancellationToken.None);
        var trackersDir = Path.Combine(dataRoot, "trackers");
        Directory.CreateDirectory(trackersDir);
        await File.WriteAllTextAsync(Path.Combine(trackersDir, "existingtracker.yml"), ValidYaml("existingtracker"));
        catalog.Reload();
        source.Definitions.Add(new RemoteTrackerDefinition("existingtracker", ValidYaml("existingtracker") + "\n# a fixed selector\n"));
        source.Definitions.Add(new RemoteTrackerDefinition("newtracker", ValidYaml("newtracker")));

        await service.UpdateNowAsync(CancellationToken.None);

        var recorded = Assert.Single(activity.Recorded);
        Assert.Contains("added the newtracker tracker definition", recorded.Message);
        Assert.Contains("updated the existingtracker tracker definition", recorded.Message);
    }

    [Fact]
    public async Task An_unchanged_definition_is_not_rewritten_or_logged()
    {
        var (service, store, activity, source, _, _) = Build();
        await store.SetSettingsAsync(isEnabled: true, intervalHours: 24, CancellationToken.None);
        source.Definitions.Add(new RemoteTrackerDefinition("sametracker", ValidYaml("sametracker")));

        var first = await service.UpdateNowAsync(CancellationToken.None);
        Assert.Single(first.UpdatedTrackerIds);
        Assert.Single(activity.Recorded);

        var second = await service.UpdateNowAsync(CancellationToken.None);

        Assert.Empty(second.UpdatedTrackerIds);
        Assert.Equal(TrackerDefinitionUpdateStatuses.UpToDate, second.Status);
        // Still just the one recorded activity entry from the first pass.
        Assert.Single(activity.Recorded);
    }

    [Fact]
    public async Task A_source_failure_is_recorded_without_touching_any_definition()
    {
        var (service, store, activity, source, _, _) = Build();
        await store.SetSettingsAsync(isEnabled: true, intervalHours: 24, CancellationToken.None);
        source.ThrowOnFetch = true;

        var state = await service.UpdateNowAsync(CancellationToken.None);

        Assert.Equal(TrackerDefinitionUpdateStatuses.Failed, state.Status);
        Assert.NotNull(state.Error);
        Assert.Empty(activity.Recorded);
    }

    private static string ValidYaml(string id) =>
        """
        id: __ID__
        name: __ID__
        type: public
        encoding: UTF-8
        links:
          - https://example.invalid/
        caps:
          categorymappings:
            - {id: 1, cat: Movies, desc: Movies}
          modes:
            search: [q]
        settings: []
        search:
          paths:
            - path: "search/{{ .Keywords }}/"
          rows:
            selector: tr.row
          fields:
            title:
              selector: a.name
            download:
              selector: a.mag
              attribute: href
            size:
              selector: td.size
            seeders:
              selector: td.seeders
            leechers:
              text: 0
        """.Replace("__ID__", id);

    private sealed class FakeSource : IRemoteTrackerDefinitionSource
    {
        public List<RemoteTrackerDefinition> Definitions { get; } = [];
        public bool ThrowOnFetch { get; set; }
        public bool WasCalled { get; private set; }

        public Task<IReadOnlyList<RemoteTrackerDefinition>> FetchAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            if (ThrowOnFetch)
            {
                throw new HttpRequestException("The remote tracker-definitions source did not answer.");
            }

            return Task.FromResult<IReadOnlyList<RemoteTrackerDefinition>>([.. Definitions]);
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InMemoryStore : ITrackerDefinitionUpdateStore
    {
        private TrackerDefinitionUpdateState state = new(false, 24, null, null, TrackerDefinitionUpdateStatuses.Disabled, null, [], DateTimeOffset.UtcNow);

        public Task<TrackerDefinitionUpdateState> GetAsync(CancellationToken cancellationToken) => Task.FromResult(state);

        public Task<TrackerDefinitionUpdateState> SetSettingsAsync(bool isEnabled, int intervalHours, CancellationToken cancellationToken)
        {
            state = state with
            {
                IsEnabled = isEnabled,
                IntervalHours = intervalHours,
                Status = isEnabled ? (state.LastCheckedUtc is null ? TrackerDefinitionUpdateStatuses.NeverChecked : state.Status) : TrackerDefinitionUpdateStatuses.Disabled
            };
            return Task.FromResult(state);
        }

        public Task<TrackerDefinitionUpdateState> SaveSuccessAsync(DateTimeOffset checkedUtc, IReadOnlyList<string> updatedTrackerIds, CancellationToken cancellationToken)
        {
            state = state with
            {
                LastCheckedUtc = checkedUtc,
                LastUpdatedUtc = updatedTrackerIds.Count > 0 ? checkedUtc : state.LastUpdatedUtc,
                Status = updatedTrackerIds.Count > 0 ? TrackerDefinitionUpdateStatuses.Updated : TrackerDefinitionUpdateStatuses.UpToDate,
                Error = null,
                UpdatedTrackerIds = updatedTrackerIds
            };
            return Task.FromResult(state);
        }

        public Task<TrackerDefinitionUpdateState> SaveFailureAsync(string error, DateTimeOffset checkedUtc, CancellationToken cancellationToken)
        {
            state = state with
            {
                LastCheckedUtc = checkedUtc,
                Status = TrackerDefinitionUpdateStatuses.Failed,
                Error = error,
                UpdatedTrackerIds = []
            };
            return Task.FromResult(state);
        }
    }

    private sealed class RecordingActivityFeedRepository : IActivityFeedRepository
    {
        public List<(string Category, string Message)> Recorded { get; } = [];

        public Task<IReadOnlyList<ActivityEventItem>> ListActivityAsync(
            int take, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Page<ActivityEventItem>> ListActivityPageAsync(
            PageRequest request, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ActivityEventItem> RecordActivityAsync(
            string category, string message, string? detailsJson, string? relatedJobId,
            string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
        {
            Recorded.Add((category, message));
            return Task.FromResult(new ActivityEventItem(
                Id: Guid.NewGuid().ToString("n"), Category: category, Message: message, DetailsJson: detailsJson,
                RelatedJobId: relatedJobId, RelatedEntityType: relatedEntityType, RelatedEntityId: relatedEntityId,
                CreatedUtc: DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<ActivityEventItem>> RecordActivityBatchAsync(
            IReadOnlyList<ActivityEventItem> activities, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> ClearActivityAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ActivityStatsSnapshot> GetActivityStatsAsync(
            DateTimeOffset sinceUtc, int windowHours, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
