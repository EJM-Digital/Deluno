using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Jobs.Migrations;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The user's answers to the import failure table.
///
/// <para>James, having settled all sixteen decisions: <i>"I think all these
/// things we decided need to have configuration toggles to set them on and off
/// in a management / blocklist console."</i> The right harshness depends on the
/// library — somebody on a fast line with spare disk wants it strict; somebody
/// on a flaky share does not.</para>
///
/// <para>What matters here is the <em>shape</em>: only differences are stored,
/// so a default stays a default. Storing today's answer on every reason would
/// have quietly frozen the shipped table at whatever it happened to say the
/// first time somebody opened the screen.</para>
/// </summary>
public sealed class YourOwnFailureRulesTests
{
    [Fact]
    public async Task An_installation_that_has_said_nothing_gets_the_shipped_answers()
    {
        using var storage = await StorageAsync();
        var rules = new SqliteImportFailureRuleRepository(storage.Factory, Clock);

        var listed = await rules.ListAsync(CancellationToken.None);

        Assert.Equal(ImportFailurePolicy.KnownReasons.Count, listed.Count);
        Assert.All(listed, rule => Assert.Equal(ImportFailurePolicy.BlockFor(rule.ReasonCode), rule.Decision));
        Assert.All(listed, rule => Assert.False(rule.IsOverridden));
        Assert.Empty(await rules.GetOverridesAsync(CancellationToken.None));
    }

    /// <summary>
    /// The point of the screen. A vanished source never refuses a release by
    /// default, because it is usually the client's fault rather than the file's
    /// — but somebody who keeps being handed the same rotten release can say
    /// otherwise.
    /// </summary>
    [Fact]
    public async Task An_answer_of_your_own_replaces_the_shipped_one()
    {
        using var storage = await StorageAsync();
        var rules = new SqliteImportFailureRuleRepository(storage.Factory, Clock);

        await rules.SetAsync(ImportFailurePolicy.MissingSource, BlockDecision.Immediately, CancellationToken.None);

        var overrides = await rules.GetOverridesAsync(CancellationToken.None);
        Assert.Equal(BlockDecision.Immediately, ImportFailurePolicy.BlockFor(ImportFailurePolicy.MissingSource, overrides));

        var rule = (await rules.ListAsync(CancellationToken.None))
            .Single(candidate => candidate.ReasonCode == ImportFailurePolicy.MissingSource);
        Assert.Equal(BlockDecision.Immediately, rule.Decision);
        Assert.Equal(BlockDecision.Never, rule.DefaultDecision);
        Assert.True(rule.IsOverridden);
    }

    /// <summary>It goes the softer way too, which is the more likely use.</summary>
    [Fact]
    public async Task You_can_stop_Deluno_refusing_something_it_would_refuse()
    {
        using var storage = await StorageAsync();
        var rules = new SqliteImportFailureRuleRepository(storage.Factory, Clock);

        await rules.SetAsync(ImportFailurePolicy.MediaProbeRejected, BlockDecision.Never, CancellationToken.None);

        var overrides = await rules.GetOverridesAsync(CancellationToken.None);
        Assert.False(ImportFailurePolicy.ShouldBlock(
            ImportFailurePolicy.BlockFor(ImportFailurePolicy.MediaProbeRejected, overrides),
            priorFailuresOfSameRelease: 9));
    }

    [Fact]
    public async Task Changing_your_mind_replaces_the_answer_rather_than_adding_one()
    {
        using var storage = await StorageAsync();
        var rules = new SqliteImportFailureRuleRepository(storage.Factory, Clock);

        await rules.SetAsync(ImportFailurePolicy.ImportFailed, BlockDecision.Immediately, CancellationToken.None);
        await rules.SetAsync(ImportFailurePolicy.ImportFailed, BlockDecision.AskMe, CancellationToken.None);

        var overrides = await rules.GetOverridesAsync(CancellationToken.None);
        Assert.Equal(BlockDecision.AskMe, Assert.Single(overrides).Value);
    }

    /// <summary>
    /// Reset forgets the opinion rather than writing today's default down. If
    /// it wrote the default down, a later change to the shipped table would
    /// never reach anybody who had ever pressed reset — and the whole table
    /// would be frozen by the act of restoring it.
    /// </summary>
    [Fact]
    public async Task Going_back_to_the_default_stores_nothing_at_all()
    {
        using var storage = await StorageAsync();
        var rules = new SqliteImportFailureRuleRepository(storage.Factory, Clock);

        await rules.SetAsync(ImportFailurePolicy.Permission, BlockDecision.Immediately, CancellationToken.None);
        await rules.ResetAsync(ImportFailurePolicy.Permission, CancellationToken.None);

        Assert.Empty(await rules.GetOverridesAsync(CancellationToken.None));
        var rule = (await rules.ListAsync(CancellationToken.None))
            .Single(candidate => candidate.ReasonCode == ImportFailurePolicy.Permission);
        Assert.False(rule.IsOverridden);
    }

    /// <summary>
    /// A reason invented by a newer Deluno, read by an older one. Losing the
    /// setting is acceptable; failing every import because of it is not.
    /// </summary>
    [Fact]
    public async Task An_answer_this_build_does_not_understand_is_ignored_rather_than_thrown_on()
    {
        using var storage = await StorageAsync();
        var rules = new SqliteImportFailureRuleRepository(storage.Factory, Clock);
        await rules.SetAsync(ImportFailurePolicy.LikelySample, BlockDecision.Never, CancellationToken.None);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO import_failure_rules (reason_code, decision, updated_utc) "
                + "VALUES ('somethingNewer', 'RefuseAndEmailTheIndexer', '2026-09-05T12:00:00Z');";
            await command.ExecuteNonQueryAsync();
        }

        var overrides = await rules.GetOverridesAsync(CancellationToken.None);
        Assert.Equal(BlockDecision.Never, Assert.Single(overrides).Value);
    }

    // ------------------------------------------------------------------ helpers

    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));

    private static async Task<TestStorage> StorageAsync()
    {
        var storage = TestStorage.Create();
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, Clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return storage;
    }
}
