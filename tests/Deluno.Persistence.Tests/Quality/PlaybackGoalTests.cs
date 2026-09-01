using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Data;
using Deluno.Quality.Playback;
using Deluno.Quality.ReleasePreferences;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Quality;

public sealed class PlaybackGoalTests
{
    [Fact]
    public void Every_device_goal_compiles_to_dimension_scoped_compatibility_gates()
    {
        var now = DateTimeOffset.Parse("2026-08-31T03:00:00Z");
        var livingRoom = new PlaybackDeviceProfile(
            "tv-a",
            "Living room TV",
            [
                new PlaybackCapability("video.dynamic-range.hdr10"),
                new PlaybackCapability("audio.format.eac3"),
                new PlaybackCapability("audio.channels.2-0")
            ],
            true,
            now,
            now);
        var bedroom = new PlaybackDeviceProfile(
            "tv-b",
            "Bedroom TV",
            [
                new PlaybackCapability("video.dynamic-range.dolby-vision-fallback"),
                new PlaybackCapability("audio.format.eac3-atmos"),
                new PlaybackCapability("audio.channels.5-1")
            ],
            true,
            now,
            now);
        var group = new PlaybackDeviceGroup(
            "everywhere",
            "Every screen",
            PlaybackGoalModes.EveryDevice,
            [livingRoom.Id, bedroom.Id],
            null,
            now,
            now);
        var goal = new PlaybackGoalItem(
            "goal-1",
            "Best compatible everywhere",
            "movies",
            group.Id,
            true,
            [],
            [],
            ["video.dynamic-range.dolby-vision-fallback", "video.dynamic-range.hdr10"],
            "video.dynamic-range.hdr10",
            now,
            now,
            ForbiddenTraitIds: ["video.dynamic-range.hdr10-plus"]);

        var compilation = PlaybackGoalCompiler.Compile(goal, group, [livingRoom, bedroom]);

        Assert.False(compilation.RequiresReview);
        Assert.Equal(2, compilation.Plan.CompatibilityGroups!.Count);
        Assert.Contains(
            compilation.Plan.CompatibilityGroups,
            compatibilityGroup => compatibilityGroup.Id == $"device/{livingRoom.Id}"
                && compatibilityGroup.Alternatives.Any(alternative =>
                    alternative.Contains("video.dynamic-range.hdr10")
                    && alternative.Contains("audio.format.eac3")
                    && alternative.Contains("audio.channels.2-0")));
        Assert.Empty(compilation.Plan.RequiredAnyTraitGroups ?? []);
        Assert.Contains("video.dynamic-range.hdr10-plus", compilation.Plan.ForbiddenTraitIds!);
        Assert.Equal(PreferenceIntent.Ranked, Assert.Single(compilation.Plan.Families).Intent);

        var compatible = ReleasePreferenceEvaluator.Evaluate(
            compilation.Plan,
            [
                new PreferenceFact("video.dynamic-range.hdr10", PreferenceFactState.Present),
                new PreferenceFact("audio.format.eac3", PreferenceFactState.Present),
                new PreferenceFact("audio.channels.2-0", PreferenceFactState.Present),
                new PreferenceFact("video.dynamic-range.dolby-vision-fallback", PreferenceFactState.Present),
                new PreferenceFact("audio.format.eac3-atmos", PreferenceFactState.Present),
                new PreferenceFact("audio.channels.5-1", PreferenceFactState.Present)
            ]);
        Assert.NotEqual(PreferenceEvaluationStatus.Missing, compatible.Status);

        var crossDeviceMix = ReleasePreferenceEvaluator.Evaluate(
            compilation.Plan,
            [
                new PreferenceFact("video.dynamic-range.hdr10", PreferenceFactState.Present),
                new PreferenceFact("audio.format.eac3", PreferenceFactState.Present),
                new PreferenceFact("audio.channels.5-1", PreferenceFactState.Present),
                new PreferenceFact("audio.channels.2-0", PreferenceFactState.Absent),
                new PreferenceFact("video.dynamic-range.dolby-vision-fallback", PreferenceFactState.Absent)
            ]);
        Assert.Equal(PreferenceEvaluationStatus.Missing, crossDeviceMix.Status);
    }

    [Fact]
    public void Primary_and_fallback_modes_keep_complete_device_paths_as_explicit_alternatives()
    {
        var now = DateTimeOffset.Parse("2026-08-31T03:00:00Z");
        var primary = new PlaybackDeviceProfile(
            "main-tv",
            "Main TV",
            [
                new PlaybackCapability("video.dynamic-range.hdr10"),
                new PlaybackCapability("audio.format.eac3"),
                new PlaybackCapability("audio.channels.2-0")
            ],
            true,
            now,
            now);
        var fallback = new PlaybackDeviceProfile(
            "tablet",
            "Tablet",
            [
                new PlaybackCapability("video.dynamic-range.sdr"),
                new PlaybackCapability("audio.format.aac"),
                new PlaybackCapability("audio.channels.2-0")
            ],
            true,
            now,
            now);
        var goal = new PlaybackGoalItem(
            "goal-primary",
            "Main with fallback",
            "movies",
            "mixed",
            true,
            [],
            [],
            [],
            null,
            now,
            now);

        var primaryGroup = new PlaybackDeviceGroup(
            "mixed",
            "Main and tablet",
            PlaybackGoalModes.PrimaryDevice,
            [primary.Id, fallback.Id],
            primary.Id,
            now,
            now);
        var primaryCompilation = PlaybackGoalCompiler.Compile(goal, primaryGroup, [primary, fallback]);

        var primaryPaths = Assert.Single(primaryCompilation.Plan.CompatibilityGroups!);
        Assert.Equal("primary-with-fallback/mixed", primaryPaths.Id);
        Assert.Contains(primaryPaths.Alternatives, alternative =>
            alternative.Contains("video.dynamic-range.hdr10")
            && alternative.Contains("audio.format.eac3"));
        Assert.Contains(primaryPaths.Alternatives, alternative =>
            alternative.Contains("video.dynamic-range.sdr")
            && alternative.Contains("audio.format.aac"));
        Assert.Equal(0, primaryPaths.AlternativeRanks![0]);
        Assert.Contains(1, primaryPaths.AlternativeRanks);
        Assert.False(primaryCompilation.RequiresReview);

        var primaryEvaluation = ReleasePreferenceEvaluator.Evaluate(
            primaryCompilation.Plan,
            [
                new PreferenceFact("video.dynamic-range.hdr10", PreferenceFactState.Present),
                new PreferenceFact("audio.format.eac3", PreferenceFactState.Present),
                new PreferenceFact("audio.channels.2-0", PreferenceFactState.Present)
            ]);
        var fallbackEvaluation = ReleasePreferenceEvaluator.Evaluate(
            primaryCompilation.Plan,
            [
                new PreferenceFact("video.dynamic-range.sdr", PreferenceFactState.Present),
                new PreferenceFact("audio.format.aac", PreferenceFactState.Present),
                new PreferenceFact("audio.channels.2-0", PreferenceFactState.Present)
            ]);
        Assert.True(primaryEvaluation.HardGatesPassed);
        Assert.True(fallbackEvaluation.HardGatesPassed);
        Assert.Equal(-1, ReleasePreferenceEvaluator.CompareForSelection(
            primaryCompilation.Plan, primaryEvaluation, fallbackEvaluation));
        Assert.Equal(1, ReleasePreferenceEvaluator.CompareForSelection(
            primaryCompilation.Plan, fallbackEvaluation, primaryEvaluation));

        var restoredPlan = ReleasePreferencePlanCodec.Deserialize(
            ReleasePreferencePlanCodec.Serialize(primaryCompilation.Plan));
        var restoredPaths = Assert.Single(restoredPlan.CompatibilityGroups!);
        Assert.Equal(primaryCompilation.PlanHash, restoredPlan.PlanHash);
        Assert.Equal(primaryPaths.AlternativeRanks, restoredPaths.AlternativeRanks);
        Assert.Equal(
            ReleasePreferencePlanCodec.Serialize(primaryCompilation.Plan),
            ReleasePreferencePlanCodec.Serialize(restoredPlan));

        var fallbackGroup = primaryGroup with { Mode = PlaybackGoalModes.Fallback };
        var fallbackCompilation = PlaybackGoalCompiler.Compile(goal, fallbackGroup, [primary, fallback]);
        Assert.Equal("fallback/mixed", Assert.Single(fallbackCompilation.Plan.CompatibilityGroups!).Id);
        Assert.False(fallbackCompilation.RequiresReview);
    }

    [Fact]
    public void Unknown_device_capability_requires_review_and_is_never_compiled_as_absent()
    {
        var now = DateTimeOffset.Parse("2026-08-31T03:00:00Z");
        var device = new PlaybackDeviceProfile(
            "tv-unknown",
            "Unmeasured TV",
            [new PlaybackCapability("video.dynamic-range.dolby-vision", PlaybackCapabilityStates.Unknown, "probe")],
            true,
            now,
            now);
        var group = new PlaybackDeviceGroup("one", "One screen", PlaybackGoalModes.PrimaryDevice, [device.Id], device.Id, now, now);
        var goal = new PlaybackGoalItem("goal-2", "Review unknown", "tv", group.Id, true, [], [], [], null, now, now);

        var compilation = PlaybackGoalCompiler.Compile(goal, group, [device]);

        Assert.True(compilation.RequiresReview);
        Assert.Contains(compilation.UnknownCapabilities, item => item.Contains("Unmeasured TV", StringComparison.Ordinal));
        Assert.Empty(compilation.Plan.RequiredAnyTraitGroups ?? []);
    }

    [Fact]
    public void Goal_validation_rejects_conflicting_intent_before_save()
    {
        var now = DateTimeOffset.Parse("2026-08-31T03:00:00Z");
        var goal = new PlaybackGoalItem(
            "conflict",
            "Conflicting goal",
            "movies",
            "group",
            false,
            ["video.dynamic-range.hdr10"],
            [["video.dynamic-range.hdr10", "video.dynamic-range.sdr"]],
            ["video.dynamic-range.dolby-vision"],
            "video.dynamic-range.hdr10",
            now,
            now,
            ["video.dynamic-range.hdr10", "video.dynamic-range.sdr"]);

        var errors = PlaybackGoalValidator.Validate(goal, null, []);

        Assert.Contains(errors, error => error.Contains("both require and forbid", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("every trait is forbidden", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("must be one of the preferred traits", StringComparison.Ordinal));
    }

    [Fact]
    public void Must_play_every_device_rejects_explicitly_absent_required_capability()
    {
        var now = DateTimeOffset.Parse("2026-08-31T03:00:00Z");
        var device = new PlaybackDeviceProfile(
            "tv-a",
            "Living room TV",
            [new PlaybackCapability("video.dynamic-range.hdr10", PlaybackCapabilityStates.Absent)],
            true,
            now,
            now);
        var group = new PlaybackDeviceGroup(
            "everywhere",
            "Every screen",
            PlaybackGoalModes.EveryDevice,
            [device.Id],
            null,
            now,
            now);
        var goal = new PlaybackGoalItem(
            "goal-absent",
            "Must play",
            "movies",
            group.Id,
            true,
            ["video.dynamic-range.hdr10"],
            [],
            [],
            null,
            now,
            now);

        var errors = PlaybackGoalValidator.Validate(goal, group, [device]);

        Assert.Contains(errors, error => error.Contains("explicitly absent", StringComparison.Ordinal));
    }

    [Fact]
    public void Must_play_rejects_a_specific_capability_when_its_required_companion_is_absent()
    {
        var now = DateTimeOffset.Parse("2026-08-31T03:00:00Z");
        var device = new PlaybackDeviceProfile(
            "tv-dv",
            "Dolby Vision TV",
            [
                new PlaybackCapability("video.dynamic-range.dolby-vision-fallback", PlaybackCapabilityStates.Present),
                new PlaybackCapability("video.dynamic-range.hdr10", PlaybackCapabilityStates.Absent)
            ],
            true,
            now,
            now);
        var group = new PlaybackDeviceGroup(
            "everywhere",
            "Every screen",
            PlaybackGoalModes.EveryDevice,
            [device.Id],
            null,
            now,
            now);
        var goal = new PlaybackGoalItem(
            "goal-dv-conflict",
            "Must play with fallback",
            "movies",
            group.Id,
            true,
            ["video.dynamic-range.dolby-vision-fallback"],
            [],
            [],
            null,
            now,
            now);

        var errors = PlaybackGoalValidator.Validate(goal, group, [device]);

        Assert.Contains(errors, error => error.Contains("explicitly absent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compiler_keeps_a_contradictory_specific_capability_review_only()
    {
        var now = DateTimeOffset.Parse("2026-08-31T03:00:00Z");
        var device = new PlaybackDeviceProfile(
            "tv-dv",
            "Dolby Vision TV",
            [
                new PlaybackCapability("video.dynamic-range.dolby-vision-fallback", PlaybackCapabilityStates.Present),
                new PlaybackCapability("video.dynamic-range.hdr10", PlaybackCapabilityStates.Absent)
            ],
            true,
            now,
            now);
        var group = new PlaybackDeviceGroup(
            "everywhere",
            "Every screen",
            PlaybackGoalModes.EveryDevice,
            [device.Id],
            null,
            now,
            now);
        var goal = new PlaybackGoalItem(
            "goal-dv-review",
            "Review fallback",
            "movies",
            group.Id,
            true,
            [],
            [],
            [],
            null,
            now,
            now);

        var compilation = PlaybackGoalCompiler.Compile(goal, group, [device]);

        Assert.True(compilation.RequiresReview);
        Assert.Empty(compilation.Plan.CompatibilityGroups ?? []);
        Assert.Contains(compilation.UnknownCapabilities, item => item.Contains("dolby-vision-fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_device_capability_remains_review_only_during_validation()
    {
        var now = DateTimeOffset.Parse("2026-08-31T03:00:00Z");
        var device = new PlaybackDeviceProfile(
            "tv-unknown",
            "Unmeasured TV",
            [new PlaybackCapability("video.dynamic-range.hdr10", PlaybackCapabilityStates.Unknown)],
            true,
            now,
            now);
        var group = new PlaybackDeviceGroup(
            "one",
            "One screen",
            PlaybackGoalModes.PrimaryDevice,
            [device.Id],
            device.Id,
            now,
            now);
        var goal = new PlaybackGoalItem(
            "goal-unknown",
            "Review me",
            "movies",
            group.Id,
            true,
            ["video.dynamic-range.hdr10"],
            [],
            [],
            null,
            now,
            now);

        Assert.Empty(PlaybackGoalValidator.Validate(goal, group, [device]));
    }

    [Fact]
    public async Task Device_profiles_groups_and_goals_round_trip_as_typed_json()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T03:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlaybackGoalRepository(storage.Factory, clock);
        var profile = await repository.CreateDeviceProfileAsync(
            new CreatePlaybackDeviceProfileRequest(
                "Living room",
                [new PlaybackCapability("video.dynamic-range.hdr10", PlaybackCapabilityStates.Present, "owner", 0.95)],
                true),
            CancellationToken.None);
        var group = await repository.CreateDeviceGroupAsync(
            new CreatePlaybackDeviceGroupRequest("Everywhere", PlaybackGoalModes.EveryDevice, [profile.Id], null),
            CancellationToken.None);
        var goal = await repository.CreateGoalAsync(
            new CreatePlaybackGoalRequest(
                "Compatible movies",
                "movies",
                group.Id,
                true,
                ["video.codec.h264"],
                [["video.dynamic-range.hdr10", "video.dynamic-range.sdr"]],
                ["video.dynamic-range.hdr10"],
                "video.dynamic-range.hdr10",
                ForbiddenTraitIds: ["video.dynamic-range.hdr10-plus"]),
            CancellationToken.None);

        var readProfile = Assert.Single(await repository.ListDeviceProfilesAsync(CancellationToken.None));
        var readGroup = Assert.Single(await repository.ListDeviceGroupsAsync(CancellationToken.None));
        var readGoal = Assert.Single(await repository.ListGoalsAsync(CancellationToken.None));

        Assert.Equal("video.dynamic-range.hdr10", Assert.Single(readProfile.Capabilities).TraitId);
        Assert.Equal("user", Assert.Single(readProfile.Capabilities).Source);
        Assert.NotNull(Assert.Single(readProfile.Capabilities).LastConfirmedUtc);
        Assert.Equal(PlaybackGoalModes.EveryDevice, readGroup.Mode);
        Assert.Equal(profile.Id, Assert.Single(readGroup.DeviceProfileIds));
        Assert.Equal(group.Id, readGoal.DeviceGroupId);
        Assert.Equal("video.codec.h264", Assert.Single(readGoal.RequiredTraitIds));
        Assert.Contains(readGoal.RequiredAnyTraitGroups, values => values.Contains("video.dynamic-range.sdr"));
        Assert.Contains("video.dynamic-range.hdr10-plus", readGoal.EffectiveForbiddenTraitIds);

        Assert.True(await repository.DeleteGoalAsync(goal.Id, CancellationToken.None));
        Assert.True(await repository.DeleteDeviceGroupAsync(group.Id, CancellationToken.None));
        Assert.True(await repository.DeleteDeviceProfileAsync(profile.Id, CancellationToken.None));
    }
}
