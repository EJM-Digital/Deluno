using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Platform.Migration;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.Guides;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// Rules made from Deluno's old guide catalogue, shaped as they were found on
/// the physical rig, moved onto what each entry really is.
/// </summary>
public sealed class GuideRuleMigrationTests
{
    private const string OldAmazon = "4e9a630db98d5391aec1368a0256e2fe";
    private const string OldNetflix = "4e9a630db98d5391aec1368a0256e2ef";
    private const string OldRepackProper = "3a4127d8aa781b44120d907f2cd62627";
    private const string Old3D = "b8cd450cbfa689c0259a01d9e29ba3d6";
    private const string OldHdr10 = "7878c33f1963fefb3d6c8657d46c2f0a";
    private const string TrashUpscaledMovies = "bfd8eb01832d646a0a89c4deb46f8564";

    [Fact]
    public async Task Old_catalogue_rules_move_in_place_and_keep_every_profile_answer()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-16T00:00:00Z"));
        var (quality, service, activity) = await CreateAsync(storage, clock);

        var amazonMovies = await AddTypedAsync(quality, "Amazon Prime", "movies", 100, OldAmazon);
        var amazonTv = await AddTypedAsync(quality, "Amazon Prime", "tv", 100, OldAmazon);
        var netflix = await AddTypedAsync(quality, "My streaming favourite", "movies", 0, OldNetflix);
        var repack = await AddRegexLinesAsync(quality, "Repack / Proper", "movies", 5, OldRepackProper);
        var threeDTv = await AddRegexLinesAsync(quality, "3D", "tv", -10000, Old3D);
        var hdr10 = await AddTypedAsync(quality, "HDR10", "movies", 510, OldHdr10);
        // A genuine Radarr import that shares an old catalogue id: its
        // specifications say it is TRaSH's own "Upscaled", not old "Blu-ray".
        var upscaledSource = GuideSourceInventoryCatalog.Current.CustomFormats.Single(format => format.TrashId == TrashUpscaledMovies);
        var imported = await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest("Upscaled", "movies", -10000, TrashUpscaledMovies,
                GuideFormatConditions.FromSourceClauses(upscaledSource.MatcherClauses), true),
            CancellationToken.None);
        var profile = await quality.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "E2E Movies 1080p",
                "movies",
                "WEB 1080p",
                "WEB 1080p",
                string.Join(",", amazonMovies.Id, netflix.Id, repack.Id, hdr10.Id),
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false,
                FormatIntents: new Dictionary<string, string> { [netflix.Id] = "prefer", [amazonMovies.Id] = "strong-prefer" }),
            CancellationToken.None);

        // Before: the old catalogue's Amazon rule matched an Amazon HDTV
        // capture on the name alone.
        Assert.True(Assert.Single(CustomFormatMatcher.DryRun("Sintel.2010.1080p.AMZN.HDTV.x264-DELUNO", [amazonMovies])).IsMatch);

        var preview = await service.PreviewAsync(CancellationToken.None);
        var byRule = preview.Items.ToDictionary(item => item.RuleId);

        Assert.DoesNotContain(imported.Id, byRule.Keys);
        Assert.Equal(6, preview.Items.Count);
        AssertMove(byRule[amazonMovies.Id], GuideRuleMigrationPlanner.AdoptTrashFormat, "b3b3a6ac74ecbd56bcdbefa4799fb9df", "AMZN");
        AssertMove(byRule[amazonTv.Id], GuideRuleMigrationPlanner.AdoptTrashFormat, "d660701077794679fd59e8bdf4ce3a29", "AMZN");
        AssertMove(byRule[repack.Id], GuideRuleMigrationPlanner.AdoptTrashFormat, "e7718d7a3ce595f289bfee26adc178f5", "Repack/Proper");
        AssertMove(byRule[hdr10.Id], GuideRuleMigrationPlanner.AdoptDelunoFormat, "deluno-hdr-hdr10", "HDR10");
        Assert.Equal(["E2E Movies 1080p"], byRule[amazonMovies.Id].ProfileNames);
        Assert.False(byRule[amazonMovies.Id].ConditionsWereEdited);

        // An owner's name stays theirs; only an untouched name follows TRaSH.
        Assert.Equal("My streaming favourite", byRule[netflix.Id].NewName);
        Assert.Equal("170b1d363bd8516fbf3a3eb05d4faff6", byRule[netflix.Id].NewTrashId);

        // TRaSH has no TV 3D format: the rule stays the owner's, still
        // matching any one of its patterns without a guide id to imply it.
        var keep = byRule[threeDTv.Id];
        Assert.Equal(GuideRuleMigrationPlanner.KeepAsOwnRule, keep.Action);
        Assert.Null(keep.NewTrashId);
        Assert.Equal("3D", keep.NewName);

        var result = await service.ApplyAsync(
            new GuideRuleMigrationApplyRequest(preview.PreviewHash, preview.Items.Select(item => item.RuleId).ToArray()),
            CancellationToken.None);
        Assert.Equal(6, result.Moved);

        var after = (await quality.ListCustomFormatsAsync(CancellationToken.None)).ToDictionary(item => item.Id);
        Assert.Equal("AMZN", after[amazonMovies.Id].Name);
        Assert.Equal(100, after[amazonMovies.Id].Score);
        Assert.Equal("b3b3a6ac74ecbd56bcdbefa4799fb9df", after[amazonMovies.Id].TrashId);
        Assert.Null(after[threeDTv.Id].TrashId);
        Assert.Equal(imported.Conditions, after[imported.Id].Conditions);

        var profileAfter = Assert.Single(await quality.ListQualityProfilesAsync(CancellationToken.None));
        Assert.Equal(profile.CustomFormatIds, profileAfter.CustomFormatIds);
        Assert.Equal("strong-prefer", profileAfter.FormatIntents![amazonMovies.Id]);
        Assert.Equal("prefer", profileAfter.FormatIntents[netflix.Id]);

        // After: TRaSH's Amazon format needs a WEB source, for movies and TV.
        Assert.False(Assert.Single(CustomFormatMatcher.DryRun("Sintel.2010.1080p.AMZN.HDTV.x264-DELUNO", [after[amazonMovies.Id]])).IsMatch);
        Assert.True(Assert.Single(CustomFormatMatcher.DryRun("Sintel.2010.1080p.AMZN.WEBRip.DDP5.1.x264-DELUNO", [after[amazonMovies.Id]])).IsMatch);
        Assert.False(Assert.Single(CustomFormatMatcher.DryRun("Breaking.Bad.S01E03.1080p.AMZN.HDTV.x264-DELUNO", [after[amazonTv.Id]])).IsMatch);
        Assert.True(Assert.Single(CustomFormatMatcher.DryRun("Breaking.Bad.S01E03.1080p.AMZN.WEBRip.DDP5.1.x264-DELUNO", [after[amazonTv.Id]])).IsMatch);
        Assert.True(Assert.Single(CustomFormatMatcher.DryRun("Show.S01E01.1080p.3D.BluRay.x264-GRP", [after[threeDTv.Id]])).IsMatch);

        // Each move is in Activity with what the rule was and what it became.
        Assert.Equal(6, activity.Recorded.Count);
        var amazonActivity = Assert.Single(activity.Recorded, item => item.RelatedEntityId == amazonMovies.Id);
        Assert.Equal(GuideRuleMigrationService.ActivityCategory, amazonActivity.Category);
        Assert.Equal("custom-format", amazonActivity.RelatedEntityType);
        using var details = JsonDocument.Parse(amazonActivity.DetailsJson!);
        Assert.Equal(OldAmazon, details.RootElement.GetProperty("before").GetProperty("trashId").GetString());
        Assert.Equal("b3b3a6ac74ecbd56bcdbefa4799fb9df", details.RootElement.GetProperty("after").GetProperty("trashId").GetString());

        // Nothing is left to move.
        Assert.Empty((await service.PreviewAsync(CancellationToken.None)).Items);
    }

    [Fact]
    public async Task A_stale_preview_or_a_rule_edited_since_moves_nothing_it_did_not_show()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-16T00:00:00Z"));
        var (quality, service, activity) = await CreateAsync(storage, clock);
        var amazon = await AddTypedAsync(quality, "Amazon Prime", "movies", 100, OldAmazon);
        var netflix = await AddTypedAsync(quality, "Netflix", "movies", 0, OldNetflix);

        var preview = await service.PreviewAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new GuideRuleMigrationApplyRequest("not-this-preview", [amazon.Id]),
            CancellationToken.None));

        // Only the chosen rule moves.
        var result = await service.ApplyAsync(
            new GuideRuleMigrationApplyRequest(preview.PreviewHash, [amazon.Id]),
            CancellationToken.None);
        Assert.Equal(1, result.Moved);
        var after = (await quality.ListCustomFormatsAsync(CancellationToken.None)).ToDictionary(item => item.Id);
        Assert.Equal(OldNetflix, after[netflix.Id].TrashId);
        Assert.Single(activity.Recorded);

        // The earlier preview no longer describes the rules.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new GuideRuleMigrationApplyRequest(preview.PreviewHash, [netflix.Id]),
            CancellationToken.None));

        // An owner who edits the rule between preview and apply keeps the edit.
        var fresh = await service.PreviewAsync(CancellationToken.None);
        var edited = await quality.ReplaceCustomFormatDefinitionAsync(
            netflix.Id, netflix.Name, netflix.TrashId, "[{\"type\":\"releaseTitle\",\"value\":\"\\\\bNFLX\\\\b\",\"negate\":false,\"required\":true}]",
            netflix.Conditions, CancellationToken.None);
        Assert.NotNull(edited);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new GuideRuleMigrationApplyRequest(fresh.PreviewHash, [netflix.Id]),
            CancellationToken.None));
    }

    [Fact]
    public void Old_catalogue_conditions_are_read_in_every_shape_it_wrote_them()
    {
        Assert.Equal(["\\bAMZN\\b", "\\bAmazon\\b"], GuideRuleMigrationPlanner.ReadTitlePatterns("[\"\\\\bAMZN\\\\b\",\"\\\\bAmazon\\\\b\"]"));
        Assert.Equal(["\\bAMZN\\b", "\\bAmazon\\b"], GuideRuleMigrationPlanner.ReadTitlePatterns("regex: \\bAMZN\\b\r\nregex: \\bAmazon\\b"));
        Assert.Equal(["\\bAMZN\\b"], GuideRuleMigrationPlanner.ReadTitlePatterns("[{\"type\":\"releaseTitle\",\"value\":\"\\\\bAMZN\\\\b\",\"negate\":false,\"required\":true}]"));
        Assert.Null(GuideRuleMigrationPlanner.ReadTitlePatterns("[{\"type\":\"source\",\"value\":\"web\"}]"));
        Assert.Null(GuideRuleMigrationPlanner.ReadTitlePatterns("source: web"));
    }

    private static void AssertMove(GuideRuleMigrationItem item, string action, string trashId, string name)
    {
        Assert.Equal(action, item.Action);
        Assert.Equal(trashId, item.NewTrashId);
        Assert.Equal(name, item.NewName);
        var target = GuidePackageCatalog.Current.CustomFormats.Single(format => format.TrashId == trashId);
        Assert.Equal(GuideFormatConditions.For(target), item.NewConditions);
    }

    private static async Task<CustomFormatItem> AddTypedAsync(IQualityRepository quality, string name, string mediaType, int score, string oldId)
    {
        var entry = LegacyGuideCatalogue.Current.Find(oldId)!;
        return await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(name, mediaType, score, oldId,
                JsonSerializer.Serialize(entry.LegacyPatterns.Select(pattern => new { type = "releaseTitle", value = pattern, negate = false, required = true })),
                true),
            CancellationToken.None);
    }

    private static async Task<CustomFormatItem> AddRegexLinesAsync(IQualityRepository quality, string name, string mediaType, int score, string oldId)
    {
        var entry = LegacyGuideCatalogue.Current.Find(oldId)!;
        return await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(name, mediaType, score, oldId,
                string.Join("\n", entry.LegacyPatterns.Select(pattern => $"regex: {pattern}")),
                true),
            CancellationToken.None);
    }

    private static async Task<(SqliteQualityRepository Quality, GuideRuleMigrationService Service, RecordingActivityFeed Activity)> CreateAsync(
        TestStorage storage,
        TimeProvider clock)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var quality = new SqliteQualityRepository(storage.Factory, clock);
        var activity = new RecordingActivityFeed();
        var service = new GuideRuleMigrationService(quality, new SqliteGuidePackageStore(storage.Factory, clock), activity);
        return (quality, service, activity);
    }

    private sealed class RecordingActivityFeed : IActivityFeedRepository
    {
        public List<ActivityEventItem> Recorded { get; } = [];

        public Task<ActivityEventItem> RecordActivityAsync(
            string category, string message, string? detailsJson, string? relatedJobId,
            string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
        {
            var item = new ActivityEventItem(Guid.NewGuid().ToString("N"), category, message, detailsJson, relatedJobId, relatedEntityType, relatedEntityId, DateTimeOffset.UtcNow);
            Recorded.Add(item);
            return Task.FromResult(item);
        }

        public Task<IReadOnlyList<ActivityEventItem>> ListActivityAsync(int take, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Page<ActivityEventItem>> ListActivityPageAsync(PageRequest request, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ActivityEventItem>> RecordActivityBatchAsync(IReadOnlyList<ActivityEventItem> activities, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> PruneActivityAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> ClearActivityAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
