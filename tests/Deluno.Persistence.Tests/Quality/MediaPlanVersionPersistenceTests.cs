using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Quality;

public sealed class MediaPlanVersionPersistenceTests
{
    [Fact]
    public async Task Create_and_update_append_immutable_snapshots_in_order()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T03:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteQualityRepository(storage.Factory, clock);
        var preferenceReference = new ReleasePreferencePlanReference(
            "quality-profile/living-room",
            "guide/v1",
            "abcdef123456");
        var profile = await repository.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "Living room quality",
                "movies",
                "WEB 1080p",
                "WEB 1080p",
                null,
                true,
                false,
                preferenceReference),
            CancellationToken.None);
        var created = await repository.CreatePolicySetAsync(
            new CreatePolicySetRequest(
                "Living room",
                "movies",
                profile.Id,
                null,
                "cf-b, cf-a",
                12,
                6,
                true,
                true,
                "Keep the family plan stable.",
                AutomationIntent: new MediaPlanAutomationIntent(
                    ScenarioId: "Family-1080P",
                    ScenarioVersion: 2,
                    SizeTierId: "balanced",
                    SizeTierName: "Balanced",
                    SizeDescription: "Typical 1080p files",
                    SubtitleIntent: "Use library preferences",
                    RoutingIntent: "Use healthy sources",
                    SharingIntent: "Inherit source policy",
                    CleanupIntent: "Verify before cleanup",
                    NotificationIntent: "Notify on attention",
                    NamingIntent: "Use library naming")),
            CancellationToken.None);

        var first = Assert.Single(await repository.ListMediaPlanVersionsAsync(created.Id, CancellationToken.None));
        Assert.Equal(1, first.Version);
        Assert.Equal("create", first.ChangeKind);
        Assert.Equal("cf-a,cf-b", first.Snapshot.CustomFormatIds);
        Assert.Equal("family-1080p", first.Snapshot.AutomationIntent!.ScenarioId);
        Assert.Equal("balanced", first.Snapshot.AutomationIntent.SizeTierId);
        Assert.Equal(preferenceReference, first.Snapshot.ReleasePreferencePlan);
        Assert.Equal(preferenceReference, created.ReleasePreferencePlan);

        var updated = await repository.UpdatePolicySetAsync(
            created.Id,
            new UpdatePolicySetRequest(
                "Living room 4K",
                "movies",
                profile.Id,
                null,
                "cf-a,cf-b",
                24,
                6,
                true,
                true,
                first.Snapshot.Notes,
                AutomationIntent: first.Snapshot.AutomationIntent),
            CancellationToken.None);

        Assert.NotNull(updated);
        var versions = await repository.ListMediaPlanVersionsAsync(created.Id, CancellationToken.None);
        Assert.Equal([2, 1], versions.Select(version => version.Version));
        Assert.Equal("update", versions[0].ChangeKind);
        Assert.NotEqual(versions[0].PlanHash, versions[1].PlanHash);
        Assert.Equal("Living room", versions[1].Snapshot.Name);
        Assert.Equal("Living room 4K", versions[0].Snapshot.Name);
        Assert.Equal(24, versions[0].Snapshot.SearchIntervalOverrideHours);
        Assert.Equal("balanced", versions[0].Snapshot.AutomationIntent!.SizeTierId);
        Assert.Equal(preferenceReference, versions[0].Snapshot.ReleasePreferencePlan);
    }

    [Fact]
    public void Diff_is_deterministic_and_only_reports_changed_fields()
    {
        var current = new MediaPlanSnapshot(
            "Plan",
            "movies",
            "quality-1",
            null,
            "cf-a",
            12,
            6,
            true,
            true,
            null);
        var proposed = current with
        {
            SearchIntervalOverrideHours = 24,
            UpgradeUntilCutoff = false,
            Notes = "Owner reviewed"
        };

        var changes = MediaPlanVersionCodec.Diff(current, proposed);

        Assert.Equal(["searchIntervalOverrideHours", "upgradeUntilCutoff", "notes"], changes.Select(change => change.Field));
        Assert.Equal(MediaPlanVersionCodec.ComputeHash(current), MediaPlanVersionCodec.ComputeHash(current));
        Assert.NotEqual(MediaPlanVersionCodec.ComputeHash(current), MediaPlanVersionCodec.ComputeHash(proposed));
    }

    [Fact]
    public void Automation_intent_is_canonical_and_participates_in_plan_diff()
    {
        var current = new MediaPlanSnapshot(
            "Plan",
            "movies",
            null,
            null,
            string.Empty,
            null,
            null,
            true,
            true,
            null,
            new MediaPlanAutomationIntent("Family-1080P", 2, " BALANCED ", " Balanced "));
        var proposed = current with
        {
            AutomationIntent = new MediaPlanAutomationIntent("family-1080p", 2, "balanced", "Balanced")
        };

        Assert.Equal(MediaPlanVersionCodec.ComputeHash(current), MediaPlanVersionCodec.ComputeHash(proposed));
        Assert.Empty(MediaPlanVersionCodec.Diff(current, proposed));
        Assert.Null(MediaPlanAutomationIntentCodec.Normalize(new MediaPlanAutomationIntent()));
    }

    [Fact]
    public async Task Existing_plan_gets_a_baseline_before_its_first_edit()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T03:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteQualityRepository(storage.Factory, clock);
        var created = await repository.CreatePolicySetAsync(
            new CreatePolicySetRequest("Plan", "movies", null, null, null, null, null, true, true, null),
            CancellationToken.None);

        // Simulate a plan row created by a release before V0038 by removing its
        // history; the first subsequent edit must still be reversible.
        await using (var connection = await storage.Factory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM media_plan_versions WHERE plan_id = @planId;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@planId";
            parameter.Value = created.Id;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync();
        }

        await repository.UpdatePolicySetAsync(
            created.Id,
            new UpdatePolicySetRequest("Edited", "movies", null, null, null, null, null, true, true, null),
            CancellationToken.None);

        var versions = await repository.ListMediaPlanVersionsAsync(created.Id, CancellationToken.None);
        Assert.Equal([2, 1], versions.Select(version => version.Version));
        Assert.Equal("baseline", versions[1].ChangeKind);
        Assert.Equal("Plan", versions[1].Snapshot.Name);
    }

    [Fact]
    public async Task Retrying_an_unchanged_update_does_not_create_a_new_plan_version()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T03:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteQualityRepository(storage.Factory, clock);
        var created = await repository.CreatePolicySetAsync(
            new CreatePolicySetRequest(
                "Plan",
                "movies",
                null,
                null,
                "cf-b, cf-a",
                12,
                6,
                true,
                true,
                "Notes",
                new MediaPlanAutomationIntent("Family-1080P", 2, "balanced")),
            CancellationToken.None);

        var updated = await repository.UpdatePolicySetAsync(
            created.Id,
            new UpdatePolicySetRequest(
                "Plan",
                "movies",
                null,
                null,
                "cf-a,cf-b",
                12,
                6,
                true,
                true,
                "Notes",
                new MediaPlanAutomationIntent("family-1080p", 2, "balanced")),
            CancellationToken.None);

        Assert.NotNull(updated);
        var versions = await repository.ListMediaPlanVersionsAsync(created.Id, CancellationToken.None);
        Assert.Single(versions);
        Assert.Equal("create", versions[0].ChangeKind);
    }

    [Fact]
    public async Task Reviewed_update_rejects_a_stale_plan_hash_without_overwriting_newer_work()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T03:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteQualityRepository(storage.Factory, clock);
        var created = await repository.CreatePolicySetAsync(
            new CreatePolicySetRequest("Plan", "movies", null, null, null, null, null, true, true, null),
            CancellationToken.None);
        var reviewedHash = (await repository.GetLatestMediaPlanVersionAsync(created.Id, CancellationToken.None))!.PlanHash;

        var updated = await repository.UpdatePolicySetAsync(
            created.Id,
            new UpdatePolicySetRequest("Newer plan", "movies", null, null, null, null, null, true, true, null),
            CancellationToken.None);
        Assert.NotNull(updated);

        var conflict = await Assert.ThrowsAsync<MediaPlanVersionConflictException>(() => repository.UpdatePolicySetAsync(
            created.Id,
            new UpdatePolicySetRequest("Stale plan", "movies", null, null, null, null, null, true, true, null),
            CancellationToken.None,
            expectedPlanHash: reviewedHash));

        Assert.Equal(created.Id, conflict.PlanId);
        var current = Assert.Single(await repository.ListPolicySetsAsync(CancellationToken.None));
        Assert.Equal("Newer plan", current.Name);
        Assert.Equal([2, 1], (await repository.ListMediaPlanVersionsAsync(created.Id, CancellationToken.None)).Select(item => item.Version));
    }
}
