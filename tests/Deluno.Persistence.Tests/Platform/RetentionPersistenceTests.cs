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
    public async Task A_rule_saved_the_way_every_rule_used_to_be_still_deletes_syncs_and_does_not_wait_on_playback()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);

        var saved = await repository.SaveRuleAsync(null, new SaveRetentionRuleRequest("Old", RetentionLevels.Movie, true, [RetentionStep.KeepOnly("server.watchedBy:anyone:")], ["server-1"], true), CancellationToken.None);

        Assert.Equal((RetentionExpiryActions.Delete, (string?)null, false, false), (saved!.ExpiryAction, saved.ExpiryTag, saved.KeepInDelunoOnly, saved.DeferWhilePlaying));
    }

    [Fact]
    public async Task A_rules_expiry_action_tag_and_switches_are_stored_and_a_request_that_says_nothing_keeps_them()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);
        var steps = new[] { RetentionStep.KeepOnly("server.watchedBy:anyone:") };

        var saved = await repository.SaveRuleAsync(null, new SaveRetentionRuleRequest("Tagger", RetentionLevels.Movie, true, steps, ["server-1"], false,
            ExpiryAction: RetentionExpiryActions.Tag, ExpiryTag: " watched-and-old ", KeepInDelunoOnly: true, DeferWhilePlaying: true), CancellationToken.None);
        Assert.Equal((RetentionExpiryActions.Tag, "watched-and-old", true, true), (saved!.ExpiryAction, saved.ExpiryTag, saved.KeepInDelunoOnly, saved.DeferWhilePlaying));

        // A saved-again rule that does not mention them (an older client) is not turned back into a deleting one.
        var again = await repository.SaveRuleAsync(saved.Id, new SaveRetentionRuleRequest("Tagger", RetentionLevels.Movie, true, steps, ["server-1"], false), CancellationToken.None);
        Assert.Equal((RetentionExpiryActions.Tag, "watched-and-old", true, true), (again!.ExpiryAction, again.ExpiryTag, again.KeepInDelunoOnly, again.DeferWhilePlaying));

        var changed = await repository.SaveRuleAsync(saved.Id, new SaveRetentionRuleRequest("Tagger", RetentionLevels.Movie, true, steps, ["server-1"], false, ExpiryAction: RetentionExpiryActions.Unmonitor, KeepInDelunoOnly: false), CancellationToken.None);
        Assert.Equal((RetentionExpiryActions.Unmonitor, false), (changed!.ExpiryAction, changed.KeepInDelunoOnly));
    }

    [Fact]
    public async Task A_mark_remembers_that_a_person_added_it_by_hand_and_that_its_day_was_done()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);
        var rule = await repository.SaveRuleAsync(null, new SaveRetentionRuleRequest("Watched", RetentionLevels.Movie, true, [RetentionStep.KeepOnly("server.watchedBy:anyone:")], ["server-1"], true), CancellationToken.None);
        var mark = new RetentionMark(rule!.Id, rule.Name, "m1", RetentionLevels.Movie, null, null, [], "Nosferatu", 1922, 1000, ["Added to the list by hand."],
            Now, Now.AddDays(14), RetentionMarkStates.Leaving, null, Now) { Manual = true };

        await repository.UpsertMarkAsync(mark, CancellationToken.None);
        await repository.UpsertMarkAsync(mark with { State = RetentionMarkStates.Done, StateReason = "Tagged." }, CancellationToken.None);

        var read = Assert.Single(await repository.ListMarksAsync(null, CancellationToken.None));
        Assert.Equal((true, RetentionMarkStates.Done, "Tagged."), (read.Manual, read.State, read.StateReason));
    }

    [Fact]
    public async Task A_mark_keeps_its_first_matched_day_and_goes_with_its_rule()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);
        var rule = await repository.SaveRuleAsync(null, new SaveRetentionRuleRequest("Watched", RetentionLevels.Movie, true, [RetentionStep.KeepOnly("server.watchedBy:anyone:")], ["server-1"], true), CancellationToken.None);
        var mark = new RetentionMark(rule!.Id, rule.Name, "m1", RetentionLevels.Movie, null, null, [], "Nosferatu", 1922, 1000, ["Watched by anyone: owner"],
            Now, Now.AddDays(14), RetentionMarkStates.Leaving, null, Now);

        await repository.UpsertMarkAsync(mark, CancellationToken.None);
        await repository.UpsertMarkAsync(mark with { FirstMatchedUtc = Now.AddDays(5), State = RetentionMarkStates.Ready, Reasons = ["again"] }, CancellationToken.None);

        var read = Assert.Single(await repository.ListMarksAsync(null, CancellationToken.None));
        Assert.Equal((Now, RetentionMarkStates.Ready, "Watched"), (read.FirstMatchedUtc, read.State, read.RuleName));
        Assert.Equal(["again"], read.Reasons);
        Assert.Empty(read.ServerItems);

        await repository.UpsertMarkAsync(read with { ServerItems = [new RetentionServerItem("server-1", "p1", "movie", "3")] }, CancellationToken.None);
        Assert.Equal([new RetentionServerItem("server-1", "p1", "movie", "3")], Assert.Single(await repository.ListMarksAsync(null, CancellationToken.None)).ServerItems);

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

    [Fact]
    public async Task A_collection_failure_keeps_when_it_last_worked()
    {
        using var storage = TestStorage.Create();
        var repository = await RepositoryAsync(storage);

        await repository.SaveCollectionStateAsync(new RetentionCollectionState("server-1", "movies", 2, 0, null, Now, Now), CancellationToken.None);
        await repository.SaveCollectionStateAsync(new RetentionCollectionState("server-1", "movies", 3, 1, "Deluno could not reach Rig lab Emby.", Now.AddHours(8), null), CancellationToken.None);

        var state = Assert.Single(await repository.ListCollectionStatesAsync(CancellationToken.None));
        Assert.Equal((3, 1, "Deluno could not reach Rig lab Emby.", Now.AddHours(8), (DateTimeOffset?)Now), (state.Members, state.Skipped, state.Problem, state.AttemptedUtc, state.SyncedUtc));
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
