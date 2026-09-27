using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #905's persistence surface for the rule engine: saving, listing and
/// deleting rules; overlap refusal round-tripping through the repository the
/// endpoints call; the ignore list (#817); and purge-all (#826). The
/// evaluator behaviour itself (which rule fires when) is covered by
/// <see cref="DownloadHealthRulesAreConfigurableTests"/> - this file is about
/// what a person editing Settings -> Download health actually persists.
/// </summary>
public sealed class DownloadHealthRulesApiSurfaceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z");

    [Fact]
    public async Task First_read_on_a_fresh_install_synthesizes_the_legacy_toggles_and_nothing_else_is_on()
    {
        using var world = await World.CreateAsync();

        var rules = await world.HealthRepository.GetDownloadHealthRulesAsync(await world.LegacySettingsAsync(), CancellationToken.None);

        // #936: nothing new is switched on for an existing install - only
        // whatever the flat toggles already had enabled carries over, and the
        // brand-new "dead torrent" kind (no legacy equivalent) stays off.
        var deadTorrent = Assert.Single(rules, rule => rule.Kind == DownloadHealthRuleKinds.DeadTorrent);
        Assert.False(deadTorrent.Enabled);
    }

    [Fact]
    public async Task Saving_a_new_rule_persists_it_and_a_second_read_sees_it()
    {
        using var world = await World.CreateAsync();
        var legacy = await world.LegacySettingsAsync();
        var rule = TooSlowRule("new-rule", enabled: true, min: 0, max: 49);

        var (saved, error) = await world.HealthRepository.SaveDownloadHealthRuleAsync(rule, legacy, CancellationToken.None);

        Assert.Null(error);
        Assert.Contains(saved, candidate => candidate.Id == "new-rule");

        var reread = await world.HealthRepository.GetDownloadHealthRulesAsync(legacy, CancellationToken.None);
        Assert.Contains(reread, candidate => candidate.Id == "new-rule");
    }

    [Fact]
    public async Task Saving_a_rule_that_overlaps_an_existing_enabled_rule_of_the_same_kind_is_refused_and_not_persisted()
    {
        using var world = await World.CreateAsync();
        var legacy = await world.LegacySettingsAsync();
        var (_, firstError) = await world.HealthRepository.SaveDownloadHealthRuleAsync(
            TooSlowRule("first", enabled: true, min: 0, max: 100), legacy, CancellationToken.None);
        Assert.Null(firstError);

        var (afterConflict, error) = await world.HealthRepository.SaveDownloadHealthRuleAsync(
            TooSlowRule("second", enabled: true, min: 0, max: 50), legacy, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Contains("overlaps", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(afterConflict, candidate => candidate.Id == "second");
    }

    [Fact]
    public async Task Deleting_a_rule_removes_only_that_rule()
    {
        using var world = await World.CreateAsync();
        var legacy = await world.LegacySettingsAsync();
        await world.HealthRepository.SaveDownloadHealthRuleAsync(TooSlowRule("keep-me", enabled: true, min: 0, max: 49), legacy, CancellationToken.None);
        await world.HealthRepository.SaveDownloadHealthRuleAsync(TooSlowRule("remove-me", enabled: true, min: 50, max: 100), legacy, CancellationToken.None);

        var remaining = await world.HealthRepository.DeleteDownloadHealthRuleAsync("remove-me", CancellationToken.None);

        Assert.Contains(remaining, rule => rule.Id == "keep-me");
        Assert.DoesNotContain(remaining, rule => rule.Id == "remove-me");
    }

    [Fact]
    public async Task An_ignore_list_entry_can_be_added_and_removed()
    {
        using var world = await World.CreateAsync();
        var entry = new DownloadHealthIgnoreEntry("entry-1", Hash: "abc123", Category: null, TrackerDomain: null, Note: "Known-slow seedbox", CreatedUtc: default);

        var afterAdd = await world.HealthRepository.AddDownloadHealthIgnoreEntryAsync(entry, CancellationToken.None);
        Assert.Contains(afterAdd, item => item.Id == "entry-1" && item.Hash == "abc123");

        var afterRemove = await world.HealthRepository.RemoveDownloadHealthIgnoreEntryAsync("entry-1", CancellationToken.None);
        Assert.DoesNotContain(afterRemove, item => item.Id == "entry-1");
    }

    [Fact]
    public async Task Purge_all_strikes_clears_every_record_and_reports_how_many()
    {
        using var world = await World.CreateAsync();
        await world.HealthRepository.RecordDownloadHealthObservationsAsync(
            "client-1",
            ["queue-1"],
            [new DownloadHealthObservation("client-1", "queue-1", "Example.Movie.2026", DownloadHealthStrikes.NoThroughput, "warning", "0 MB/s")],
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(6),
            CancellationToken.None);
        Assert.Single(await world.HealthRepository.ListDownloadHealthRecordsAsync(10, CancellationToken.None));

        var purged = await world.HealthRepository.PurgeAllDownloadHealthStrikesAsync(CancellationToken.None);

        Assert.Equal(1, purged);
        Assert.Empty(await world.HealthRepository.ListDownloadHealthRecordsAsync(10, CancellationToken.None));
    }

    private static DownloadHealthRule TooSlowRule(string id, bool enabled, int min, int max) => new(
        Id: id,
        Kind: DownloadHealthRuleKinds.TooSlow,
        Enabled: enabled,
        StrikeLimit: 3,
        TimerMinutes: 30,
        AppliesTo: DownloadHealthAppliesTo.Both,
        MinCompletionPercent: min,
        MaxCompletionPercent: max,
        ResetStrikesOnProgress: true,
        ActionPreset: DownloadHealthActionPresets.JustTellMe,
        CreatedUtc: DateTimeOffset.MinValue,
        UpdatedUtc: DateTimeOffset.MinValue,
        Threshold: 100);

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly FixedTimeProvider _time;

        private World(TestStorage storage, FixedTimeProvider time, SqlitePlatformSettingsRepository settingsRepository, SqliteDownloadHealthRepository healthRepository)
        {
            _storage = storage;
            _time = time;
            SettingsRepository = settingsRepository;
            HealthRepository = healthRepository;
        }

        public SqlitePlatformSettingsRepository SettingsRepository { get; }

        public SqliteDownloadHealthRepository HealthRepository { get; }

        public Task<PlatformSettingsSnapshot> LegacySettingsAsync() => SettingsRepository.GetAsync(CancellationToken.None);

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(Now);
            await new PlatformSchemaInitializer(
                storage.Factory,
                new SqliteDatabaseMigrator(storage.Factory, time),
                NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var settingsRepository = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);
            return new World(storage, time, settingsRepository, healthRepository);
        }

        public void Dispose() => _storage.Dispose();
    }
}
