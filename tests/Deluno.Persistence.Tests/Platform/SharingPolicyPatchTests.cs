using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// How a settings PATCH treats the sharing rule (#288).
///
/// These exist because the first cut got it wrong in both directions, found
/// live: a PATCH that never mentioned sharing silently cleared both targets,
/// because the repository wrote them on every save and an untouched patch
/// carries neither; and a PATCH that did mention them dropped the values on
/// the floor, because the patch DTO and its merger did not carry the fields at
/// all. Both look to a user like the settings screen quietly not saving.
/// </summary>
public sealed class SharingPolicyPatchTests
{
    private static async Task<(SqlitePlatformSettingsRepository Repository, TestStorage Storage)> CreateAsync()
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T01:02:03Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return (new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage)), storage);
    }

    /// <summary>Applies a patch the way the endpoint does: merge onto current, then save.</summary>
    private static async Task<PlatformSettingsSnapshot> PatchAsync(
        SqlitePlatformSettingsRepository repository,
        PatchPlatformSettingsRequest patch)
    {
        var current = await repository.GetAsync(CancellationToken.None);
        return await repository.SaveAsync(PlatformSettingsPatchMerger.Apply(current, patch), CancellationToken.None);
    }

    [Fact]
    public async Task A_fresh_install_ships_a_rule_that_is_safe_without_being_configured()
    {
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        var settings = await repository.GetAsync(CancellationToken.None);

        Assert.Equal(SharingPolicy.ModeShareThenTidy, settings.SharingMode);
        Assert.Equal(72, settings.SharingForHours);
        Assert.Null(settings.SharingUntilRatio);
        Assert.Equal(SharingPolicy.StuckGiveUp, settings.SharingStuckAction);
        Assert.Equal(14, settings.SharingStuckAfterDays);
    }

    [Fact]
    public async Task Setting_the_rule_persists_both_targets()
    {
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        await PatchAsync(repository, new PatchPlatformSettingsRequest(
            SharingMode: SharingPolicy.ModeShareThenTidy,
            SharingForHours: 336,
            SharingUntilRatio: 2.0));

        var settings = await repository.GetAsync(CancellationToken.None);
        Assert.Equal(336, settings.SharingForHours);
        Assert.Equal(2.0, settings.SharingUntilRatio);
    }

    [Fact]
    public async Task A_patch_about_something_else_entirely_leaves_the_rule_alone()
    {
        // Saving a rename format used to wipe both targets.
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        await PatchAsync(repository, new PatchPlatformSettingsRequest(
            SharingMode: SharingPolicy.ModeShareThenTidy,
            SharingForHours: 336,
            SharingUntilRatio: 2.0));

        await PatchAsync(repository, new PatchPlatformSettingsRequest(RenameOnImport: true));

        var settings = await repository.GetAsync(CancellationToken.None);
        Assert.Equal(336, settings.SharingForHours);
        Assert.Equal(2.0, settings.SharingUntilRatio);
    }

    [Fact]
    public async Task Clearing_half_a_rule_stays_cleared()
    {
        // The whole rule is submitted together, so a missing target on a patch
        // that sets the mode means "this half is not part of it" and must not
        // inherit the old value back on the next read.
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        await PatchAsync(repository, new PatchPlatformSettingsRequest(
            SharingMode: SharingPolicy.ModeShareThenTidy,
            SharingForHours: 336,
            SharingUntilRatio: 2.0));

        await PatchAsync(repository, new PatchPlatformSettingsRequest(
            SharingMode: SharingPolicy.ModeShareThenTidy,
            SharingForHours: 336,
            SharingUntilRatio: null));

        var settings = await repository.GetAsync(CancellationToken.None);
        Assert.Equal(336, settings.SharingForHours);
        Assert.Null(settings.SharingUntilRatio);
    }

    [Fact]
    public async Task Switching_to_tidy_now_drops_both_targets()
    {
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        await PatchAsync(repository, new PatchPlatformSettingsRequest(
            SharingMode: SharingPolicy.ModeShareThenTidy,
            SharingForHours: 336,
            SharingUntilRatio: 2.0));

        await PatchAsync(repository, new PatchPlatformSettingsRequest(SharingMode: SharingPolicy.ModeTidyNow));

        var settings = await repository.GetAsync(CancellationToken.None);
        Assert.Equal(SharingPolicy.ModeTidyNow, settings.SharingMode);
        Assert.Null(settings.SharingForHours);
        Assert.Null(settings.SharingUntilRatio);
    }
}
