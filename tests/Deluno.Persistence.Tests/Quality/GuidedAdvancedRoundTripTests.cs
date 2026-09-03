using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.Scenarios;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #353 line 3: Guided and Advanced round-trip without losing detail.
///
/// <para>Guided is a scenario; Advanced is the Media Plan it compiles into.
/// They are the same row on purpose — the compiler emits the ordinary
/// <see cref="CreatePolicySetRequest"/> rather than a parallel structure — but
/// "the same row" is a claim about the code, and the thing that actually
/// breaks is a field the compiler sets that persistence quietly drops. The
/// scenario's whole-plan intent is the exposed part: eleven nullable strings
/// in one JSON column, where a rename or a missed normalisation loses one and
/// nothing else notices.</para>
///
/// <para>So this applies every scenario in the catalogue, for every media type
/// it declares, reads the plan back the way the Advanced screen does, and
/// compares field by field.</para>
/// </summary>
public sealed class GuidedAdvancedRoundTripTests
{
    public static TheoryData<string, string> ScenarioVariants()
    {
        var data = new TheoryData<string, string>();
        foreach (var scenario in MediaPlanScenarioCatalog.All)
        {
            foreach (var variant in scenario.Variants)
            {
                data.Add(scenario.Id, variant.MediaType);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScenarioVariants))]
    public async Task Applying_a_guided_scenario_keeps_every_field_the_advanced_plan_reads_back(
        string scenarioId,
        string mediaType)
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-03T05:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var quality = new SqliteQualityRepository(storage.Factory, clock);

        var compiled = MediaPlanScenarioCompiler.Compile(scenarioId, mediaType);
        Assert.Equal(scenarioId, compiled.ScenarioId);

        var created = await quality.CreatePolicySetAsync(compiled.PolicySet, CancellationToken.None);

        // Advanced reads the list, not the object it just wrote.
        var advanced = Assert.Single(
            await quality.ListPolicySetsAsync(CancellationToken.None),
            item => item.Id == created.Id);

        Assert.Equal(compiled.PolicySet.Name, advanced.Name);
        Assert.Equal(compiled.MediaType, advanced.MediaType);
        Assert.Equal(compiled.PolicySet.SearchIntervalOverrideHours, advanced.SearchIntervalOverrideHours);
        Assert.Equal(compiled.PolicySet.RetryDelayOverrideHours, advanced.RetryDelayOverrideHours);
        Assert.Equal(compiled.PolicySet.UpgradeUntilCutoff, advanced.UpgradeUntilCutoff);
        Assert.Equal(compiled.PolicySet.IsEnabled, advanced.IsEnabled);
        Assert.Equal(compiled.PolicySet.Notes, advanced.Notes);
        Assert.Equal(compiled.PolicySet.CustomFormatIds ?? string.Empty, advanced.CustomFormatIds);

        // The whole-plan intent, field by field. `Assert.Equal` on the record
        // would pass a rename that dropped a value into the wrong property,
        // so every one is named.
        var expected = MediaPlanAutomationIntentCodec.Normalize(compiled.PolicySet.AutomationIntent);
        Assert.NotNull(expected);
        var stored = advanced.AutomationIntent;
        Assert.NotNull(stored);
        Assert.Equal(expected!.ScenarioId, stored!.ScenarioId);
        Assert.Equal(expected.ScenarioVersion, stored.ScenarioVersion);
        Assert.Equal(expected.SizeTierId, stored.SizeTierId);
        Assert.Equal(expected.SizeTierName, stored.SizeTierName);
        Assert.Equal(expected.SizeDescription, stored.SizeDescription);
        Assert.Equal(expected.SubtitleIntent, stored.SubtitleIntent);
        Assert.Equal(expected.RoutingIntent, stored.RoutingIntent);
        Assert.Equal(expected.SharingIntent, stored.SharingIntent);
        Assert.Equal(expected.CleanupIntent, stored.CleanupIntent);
        Assert.Equal(expected.NotificationIntent, stored.NotificationIntent);
        Assert.Equal(expected.NamingIntent, stored.NamingIntent);

        // And the plan is still recognised as this scenario's, which is what
        // lets Guided offer an update instead of creating a second plan.
        Assert.True(
            MediaPlanScenarioPlanIdentity.Matches(advanced, scenarioId, mediaType),
            $"The stored plan for '{scenarioId}' ({mediaType}) is no longer identifiable as that scenario's.");

        // Now the other direction: an Advanced edit that touches one field must
        // not silently discard the guided intent behind it.
        var edited = await quality.UpdatePolicySetAsync(
            created.Id,
            new UpdatePolicySetRequest(
                Name: $"{advanced.Name} (edited)",
                MediaType: advanced.MediaType,
                QualityProfileId: advanced.QualityProfileId,
                DestinationRuleId: advanced.DestinationRuleId,
                CustomFormatIds: advanced.CustomFormatIds,
                SearchIntervalOverrideHours: advanced.SearchIntervalOverrideHours,
                RetryDelayOverrideHours: advanced.RetryDelayOverrideHours,
                UpgradeUntilCutoff: advanced.UpgradeUntilCutoff,
                IsEnabled: advanced.IsEnabled,
                Notes: advanced.Notes,
                AutomationIntent: advanced.AutomationIntent),
            CancellationToken.None);

        Assert.NotNull(edited);
        Assert.Equal($"{advanced.Name} (edited)", edited!.Name);
        Assert.Equal(expected.ScenarioId, edited.AutomationIntent?.ScenarioId);
        Assert.Equal(expected.NamingIntent, edited.AutomationIntent?.NamingIntent);
        Assert.Equal(expected.SizeDescription, edited.AutomationIntent?.SizeDescription);
    }
}
