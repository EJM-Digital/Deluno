using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.ReleasePreferences;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #351: the settings-profiles-page drawer only shows its "Advanced review"
/// (and the legacy-provenance detail inside it) when
/// <see cref="RuntimeQualityProfileCompilation.AdvancedRules"/> is non-empty.
/// The smoke test that names that drawer built its "legacy rule with no safe
/// typed translation" example from an IndexerFlagSpecification condition.
///
/// <para>#745 ("Indexer flags become a real custom-format condition, not just
/// a hint") taught <see cref="CustomFormatDefinitionMatcher"/> to read that
/// exact condition as typed, so <see cref="ReleasePreferencePlanFactory.CompileProfile"/>
/// silently stopped classifying it as <see cref="LegacyPreferenceRuleKind.UnmappedAdvanced"/> -
/// the format still compiled into an ordinary typed family, but the "Advanced
/// review" disclosure it used to populate never rendered again. The same
/// fixture drift was caught and fixed in
/// <c>MigrationAssistantServiceTests</c> (Refs #745) by swapping to
/// SizeSpecification, which stays genuinely unmapped; the smoke test needed
/// the same swap and did not get it, so it hung waiting to click a button
/// that no longer exists until the suite's 30s test timeout - which then
/// surfaced as its `finally` block's cleanup DELETE failing, not as the real
/// cause.</para>
/// </summary>
public sealed class ReleasePreferencePlanFactoryCompileProfileTests
{
    private static async Task<SqliteQualityRepository> CreateRepositoryAsync(TestStorage storage, FixedTimeProvider clock)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteQualityRepository(storage.Factory, clock);
    }

    [Fact]
    public async Task A_size_condition_custom_format_stays_unmapped_advanced()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T05:00:00Z"));
        var quality = await CreateRepositoryAsync(storage, clock);

        var format = await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(
                "Smoke-Trace-Format",
                "movies",
                4321,
                "smoke-trace-format",
                """[{"name":"Size","implementation":"SizeSpecification","fields":[{"name":"value","value":"20000"}],"negate":false,"required":true}]""",
                true),
            CancellationToken.None);

        var profile = await quality.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                Name: "Smoke-Trace-Profile",
                MediaType: "movies",
                CutoffQuality: "WEB 1080p",
                AllowedQualities: "WEB 1080p",
                CustomFormatIds: format.Id,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false,
                FormatIntents: new Dictionary<string, string> { [format.Id] = "strong-prefer" }),
            CancellationToken.None);

        var customFormats = await quality.ListCustomFormatsAsync(CancellationToken.None);
        var compilation = ReleasePreferencePlanFactory.CompileProfile(profile, customFormats);

        var advanced = Assert.Single(compilation.AdvancedRules);
        Assert.Equal(LegacyPreferenceRuleKind.UnmappedAdvanced, advanced.Kind);
        Assert.True(advanced.RequiresReview);
        Assert.True(compilation.RequiresReview);
        Assert.Equal(4321, advanced.OriginalScore);
        Assert.Equal(format.Conditions, advanced.Conditions);

        // Guards the regression as it actually presented: deleting a profile
        // built this way (custom formats, formatIntents, a compiled plan
        // fetched for it) must not hang. It never did at the repository layer -
        // the hang was the smoke test's own UI wait for a button that the
        // fixture drift made unreachable - but this keeps that path proven.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var deleted = await quality.DeleteQualityProfileAsync(profile.Id, cts.Token);
        Assert.True(deleted);
    }

    /// <summary>
    /// The other half of the same drift, the other direction: an
    /// IndexerFlagSpecification condition is real typed intent since #745, so
    /// it must never be reported as an advanced/needs-review rule.
    /// </summary>
    [Fact]
    public async Task An_indexer_flag_condition_custom_format_is_not_reported_as_advanced()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T05:00:00Z"));
        var quality = await CreateRepositoryAsync(storage, clock);

        var format = await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(
                "Smoke-Trace-IndexerFlag",
                "movies",
                4321,
                "smoke-trace-indexer-flag",
                """[{"name":"Indexer flag","implementation":"IndexerFlagSpecification","fields":[{"name":"value","value":"freeleech"}],"negate":false,"required":true}]""",
                true),
            CancellationToken.None);

        var profile = await quality.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                Name: "Smoke-Trace-IndexerFlag-Profile",
                MediaType: "movies",
                CutoffQuality: "WEB 1080p",
                AllowedQualities: "WEB 1080p",
                CustomFormatIds: format.Id,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false,
                FormatIntents: new Dictionary<string, string> { [format.Id] = "strong-prefer" }),
            CancellationToken.None);

        var customFormats = await quality.ListCustomFormatsAsync(CancellationToken.None);
        var compilation = ReleasePreferencePlanFactory.CompileProfile(profile, customFormats);

        Assert.Empty(compilation.AdvancedRules);
        Assert.False(compilation.RequiresReview);
    }
}
