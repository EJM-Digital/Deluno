using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Retention;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>#494: rules, Leaving soon marks, keeps and episode removals, stored.</summary>
public sealed class RetentionPersistenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T12:00:00Z");

    [Fact]
    public async Task A_rule_keeps_its_steps_wait_and_removal_mode()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);
        var steps = new[]
        {
            RetentionStep.KeepOnly("server.watchedBy:nobody:"),
            new RetentionStep(RetentionStep.BlockType, RetentionStep.Include, Name: "Low rated", Steps: [RetentionStep.KeepOnly("server.ratingAudience:max:5")])
        };

        var saved = await repository.SaveRuleAsync(null, new SaveRetentionRuleRequest("Unwatched", RetentionLevels.Season, true, steps, ["server-1"], true, 30, RetentionRemovalModes.Ask, 12), CancellationToken.None);

        var read = await repository.GetRuleAsync(saved!.Id, CancellationToken.None);
        Assert.Equal((RetentionLevels.Season, "tv", 30, RetentionRemovalModes.Ask, 12), (read!.Level, read.MediaType, read.WaitDays, read.RemovalMode, read.CheckIntervalHours));
        Assert.Equal("Low rated", read.Steps[1].Name);
        Assert.Equal("server.ratingAudience:max:5", read.Steps[1].Steps![0].Condition);
        Assert.Equal(Now.AddHours(12), read.NextCheckUtc);
    }

    [Fact]
    public async Task A_mark_keeps_its_first_matched_day_and_goes_with_its_rule()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);
        var rule = await repository.SaveRuleAsync(null, new SaveRetentionRuleRequest("Watched", RetentionLevels.Movie, true, [RetentionStep.KeepOnly("server.watchedBy:anyone:")], ["server-1"], true), CancellationToken.None);
        var mark = new RetentionMark(rule!.Id, rule.Name, "m1", RetentionLevels.Movie, null, null, [], "Nosferatu", 1922, 1000, ["Watched by anyone: james"],
            Now, Now.AddDays(14), RetentionMarkStates.Leaving, null, Now);

        await repository.UpsertMarkAsync(mark, CancellationToken.None);
        await repository.UpsertMarkAsync(mark with { FirstMatchedUtc = Now.AddDays(5), State = RetentionMarkStates.Ready, Reasons = ["again"] }, CancellationToken.None);

        var read = Assert.Single(await repository.ListMarksAsync(null, CancellationToken.None));
        Assert.Equal((Now, RetentionMarkStates.Ready, "Watched"), (read.FirstMatchedUtc, read.State, read.RuleName));
        Assert.Equal(["again"], read.Reasons);

        await repository.DeleteRuleAsync(rule.Id, CancellationToken.None);
        Assert.Empty(await repository.ListMarksAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Keeping_again_from_the_same_rule_updates_the_note_and_all_rules_is_a_separate_keep()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);

        await repository.SaveKeepAsync("m1", RetentionLevels.Movie, "Nosferatu", "rule-1", "first", CancellationToken.None);
        await repository.SaveKeepAsync("m1", RetentionLevels.Movie, "Nosferatu", "rule-1", "second", CancellationToken.None);
        var everywhere = await repository.SaveKeepAsync("m1", RetentionLevels.Movie, "Nosferatu", null, null, CancellationToken.None);

        var keeps = await repository.ListKeepsAsync(CancellationToken.None);
        Assert.Equal(2, keeps.Count);
        Assert.Equal("second", Assert.Single(keeps, keep => keep.RuleId == "rule-1").Note);
        Assert.NotNull(await repository.DeleteKeepAsync(everywhere.Id, CancellationToken.None));
        Assert.Single(await repository.ListKeepsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_episode_removal_is_recorded_and_undone()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);

        await repository.SaveEpisodeRemovalAsync(new RetentionEpisodeRemoval("e1", "s1", 1, 1, "rule-1", "Watched seasons", "Removed by retention rule", Now), CancellationToken.None);

        Assert.Equal("Watched seasons", Assert.Single(await repository.ListEpisodeRemovalsAsync("s1", CancellationToken.None)).RuleName);
        Assert.NotNull(await repository.DeleteEpisodeRemovalAsync("e1", CancellationToken.None));
        Assert.Empty(await repository.ListEpisodeRemovalsAsync("s1", CancellationToken.None));
    }

    private static async Task<SqliteRetentionRepository> RepositoryAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(Now);
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteRetentionRepository(storage.Factory, clock);
    }
}
