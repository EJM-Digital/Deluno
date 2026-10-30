using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Persistence.Tests.Small;

/// <summary>
/// #1268: the RSS sync marks and the seeding rules, on the real SQLite databases the host creates.
/// </summary>
public sealed class RssSyncStateAndSharingRulesTests : IDisposable
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private readonly TestStorage _storage = TestStorage.Create();
    private readonly FixedTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-01T12:00:00Z"));

    public void Dispose() => _storage.Dispose();

    private async Task<SqliteSharingRulesRepository> RulesAsync()
    {
        await MigratedDatabaseTemplate.CopyIntoAsync(_storage.DataRoot);
        return new SqliteSharingRulesRepository(_storage.Factory, _clock);
    }

    private async Task<SqliteRssSyncStateRepository> RssAsync()
    {
        await MigratedDatabaseTemplate.CopyIntoAsync(_storage.DataRoot);
        return new SqliteRssSyncStateRepository(_storage.Factory, _clock);
    }

    private static CreateSharingRuleRequest NewRule(int? priority = null, string? category = "  movies  ", string? mode = "  ratio ")
        => new(priority, category, " ", "a,b", "ALL", mode, 48, 1.5, "remove", 3, "pause", 2, 7);

    // ---- RSS sync marks -----------------------------------------------------------------

    [Fact]
    public async Task Rss_marks_start_empty_and_come_back_as_saved_by_indexer_ignoring_case()
    {
        var rss = await RssAsync();
        Assert.Empty(await rss.GetLastSeenAsync(Token));

        var first = DateTimeOffset.Parse("2026-09-30T08:00:00Z");
        await rss.SaveLastSeenAsync(new Dictionary<string, DateTimeOffset> { ["Indexer-A"] = first }, Token);
        var second = first.AddHours(5);
        await rss.SaveLastSeenAsync(new Dictionary<string, DateTimeOffset> { ["Indexer-A"] = second, ["indexer-b"] = first }, Token);

        var read = await rss.GetLastSeenAsync(Token);
        Assert.Equal(second, read["indexer-a"]);
        Assert.Equal(first, read["INDEXER-B"]);
        Assert.Equal(2, read.Count);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("null")]
    public async Task Unreadable_or_empty_rss_marks_start_over(string stored)
    {
        var rss = await RssAsync();
        await using (var connection = await _storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, Token))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ('rss-sync.last-seen.v2', @v, '2026-10-01T00:00:00.0000000+00:00');";
            SqliteRecordHelpers.AddParameter(command, "@v", stored);
            await command.ExecuteNonQueryAsync(Token);
        }

        Assert.Empty(await rss.GetLastSeenAsync(Token));
    }

    // ---- Sharing rules ------------------------------------------------------------------

    [Fact]
    public async Task A_new_rule_is_trimmed_blank_fields_are_nulled_and_priority_follows_the_last_by_ten()
    {
        var rules = await RulesAsync();

        var first = await rules.CreateAsync(NewRule(), Token);
        var second = await rules.CreateAsync(NewRule(category: null), Token);
        var explicitPriority = await rules.CreateAsync(NewRule(priority: 0), Token);
        var negative = await rules.CreateAsync(NewRule(priority: -5), Token);

        Assert.Equal(10, first.Priority);
        Assert.Equal(20, second.Priority);
        Assert.Equal(0, explicitPriority.Priority);
        Assert.Equal(30, negative.Priority);
        Assert.Equal("movies", first.CategoryPattern);
        Assert.Null(first.TrackerPattern);
        Assert.Equal("ratio", first.Mode);
        Assert.Equal(SharingRuleItem.TagMatchAll, first.TagMatchMode);
        Assert.Equal(_clock.GetUtcNow(), first.CreatedUtc);

        var listed = await rules.ListAsync(Token);
        Assert.Equal([0, 10, 20, 30], listed.Select(r => r.Priority));
        var read = listed.Single(r => r.Id == first.Id);
        Assert.Equal(first, read);
        Assert.Equal((48, 1.5, "remove", 3, "pause", 2, 7, true), (read.ForHours, read.UntilRatio, read.StuckAction, read.StuckAfterDays, read.CompletionAction, read.MinSeeders, read.MaxInactiveDays, read.IsEnabled));
    }

    [Fact]
    public async Task An_update_with_nothing_set_changes_nothing_but_the_time()
    {
        var rules = await RulesAsync();
        var created = await rules.CreateAsync(NewRule(), Token);
        _clock.Advance(TimeSpan.FromHours(1));

        var updated = await rules.UpdateAsync(created.Id.ToUpperInvariant(), new UpdateSharingRuleRequest(), Token);

        Assert.Equal(created with { UpdatedUtc = _clock.GetUtcNow() }, updated);
        Assert.Equal(updated, (await rules.ListAsync(Token)).Single());
    }

    [Fact]
    public async Task An_update_sets_each_field_it_names()
    {
        var rules = await RulesAsync();
        var created = await rules.CreateAsync(NewRule(), Token);

        var updated = await rules.UpdateAsync(created.Id, new UpdateSharingRuleRequest(
            Priority: 5, CategoryPattern: " tv ", TrackerPattern: "tracker", TagsCsv: "x", TagMatchMode: "any", Mode: "time",
            ForHours: 12, UntilRatio: 2.0, StuckAction: "stop", StuckAfterDays: 9, CompletionAction: "remove",
            MinSeeders: 4, MaxInactiveDays: 30, IsEnabled: false), Token);

        Assert.Equal(5, updated!.Priority);
        Assert.Equal("tv", updated.CategoryPattern);
        Assert.Equal("tracker", updated.TrackerPattern);
        Assert.Equal("x", updated.TagsCsv);
        Assert.Equal(SharingRuleItem.TagMatchAny, updated.TagMatchMode);
        Assert.Equal("time", updated.Mode);
        Assert.Equal(12, updated.ForHours);
        Assert.Equal(2.0, updated.UntilRatio);
        Assert.Equal("stop", updated.StuckAction);
        Assert.Equal(9, updated.StuckAfterDays);
        Assert.Equal("remove", updated.CompletionAction);
        Assert.Equal(4, updated.MinSeeders);
        Assert.Equal(30, updated.MaxInactiveDays);
        Assert.False(updated.IsEnabled);
        Assert.Equal(updated, (await rules.ListAsync(Token)).Single());
    }

    [Fact]
    public async Task An_update_clears_the_patterns_tags_policy_and_limits_it_is_told_to()
    {
        var rules = await RulesAsync();
        var created = await rules.CreateAsync(NewRule(category: "movies") with { TrackerPattern = "tr" }, Token);

        var updated = await rules.UpdateAsync(created.Id, new UpdateSharingRuleRequest(
            ClearCategoryPattern: true, ClearTrackerPattern: true, ClearTags: true, ClearPolicy: true,
            ClearMinSeeders: true, ClearMaxInactiveDays: true), Token);

        Assert.Null(updated!.CategoryPattern);
        Assert.Null(updated.TrackerPattern);
        Assert.Null(updated.TagsCsv);
        Assert.Null(updated.Mode);
        Assert.Null(updated.ForHours);
        Assert.Null(updated.UntilRatio);
        Assert.Null(updated.StuckAction);
        Assert.Null(updated.StuckAfterDays);
        Assert.Null(updated.CompletionAction);
        Assert.Null(updated.MinSeeders);
        Assert.Null(updated.MaxInactiveDays);
        Assert.Equal(created.TagMatchMode, updated.TagMatchMode);
        Assert.Equal(updated, (await rules.ListAsync(Token)).Single());
    }

    [Fact]
    public async Task Clear_flags_set_to_false_leave_the_value_alone()
    {
        var rules = await RulesAsync();
        var created = await rules.CreateAsync(NewRule(), Token);

        var updated = await rules.UpdateAsync(created.Id, new UpdateSharingRuleRequest(
            ClearCategoryPattern: false, ClearTrackerPattern: false, ClearTags: false, ClearPolicy: false,
            ClearMinSeeders: false, ClearMaxInactiveDays: false), Token);

        Assert.Equal(created.CategoryPattern, updated!.CategoryPattern);
        Assert.Equal(created.TagsCsv, updated.TagsCsv);
        Assert.Equal(created.Mode, updated.Mode);
        Assert.Equal(created.MinSeeders, updated.MinSeeders);
        Assert.Equal(created.MaxInactiveDays, updated.MaxInactiveDays);
    }

    [Fact]
    public async Task Updating_a_rule_that_does_not_exist_is_null()
    {
        var rules = await RulesAsync();

        Assert.Null(await rules.UpdateAsync("missing", new UpdateSharingRuleRequest(Priority: 1), Token));
    }

    [Fact]
    public async Task An_update_the_database_does_not_apply_is_reported_as_not_found()
    {
        var rules = await RulesAsync();
        var created = await rules.CreateAsync(NewRule(), Token);
        await using (var connection = await _storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, Token))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER skip_sharing_update BEFORE UPDATE ON sharing_rules BEGIN SELECT RAISE(IGNORE); END;";
            await command.ExecuteNonQueryAsync(Token);
        }

        Assert.Null(await rules.UpdateAsync(created.Id, new UpdateSharingRuleRequest(Priority: 1), Token));
        Assert.Equal(created.Priority, (await rules.ListAsync(Token)).Single().Priority);
    }

    [Fact]
    public async Task Deleting_a_rule_removes_it_once()
    {
        var rules = await RulesAsync();
        var created = await rules.CreateAsync(NewRule(), Token);

        Assert.True(await rules.DeleteAsync(created.Id, Token));
        Assert.False(await rules.DeleteAsync(created.Id, Token));
        Assert.Empty(await rules.ListAsync(Token));
    }

    [Fact]
    public async Task Inactive_days_count_from_the_last_time_the_download_was_active()
    {
        var rules = await RulesAsync();
        var start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

        Assert.Equal(0, await rules.RecordAndGetInactiveDaysAsync("c1", "q1", isActiveNow: false, start, Token));
        Assert.Equal(3, await rules.RecordAndGetInactiveDaysAsync("c1", "q1", isActiveNow: false, start.AddDays(3).AddHours(5), Token));
        Assert.Equal(0, await rules.RecordAndGetInactiveDaysAsync("c1", "q1", isActiveNow: true, start.AddDays(4), Token));
        Assert.Equal(1, await rules.RecordAndGetInactiveDaysAsync("c1", "q1", isActiveNow: false, start.AddDays(5), Token));
        Assert.Equal(0, await rules.RecordAndGetInactiveDaysAsync("c1", "q2", isActiveNow: true, start.AddDays(9), Token));
        Assert.Equal(0, await rules.RecordAndGetInactiveDaysAsync("c1", "q1", isActiveNow: false, start.AddDays(-1), Token));
    }
}
