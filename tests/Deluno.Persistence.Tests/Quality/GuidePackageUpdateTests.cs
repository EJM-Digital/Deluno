using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Guides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Quality;

public sealed class GuidePackageUpdateTests
{
    [Fact]
    public async Task Preview_and_apply_persist_an_immutable_active_guide_version()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var store = new SqliteGuidePackageStore(storage.Factory, time);

        var current = await store.GetCurrentAsync(CancellationToken.None);
        var proposed = current.Package with { Version = current.Package.Version + 1 };
        var preview = await store.PreviewAsync(
            new GuidePackageUpdateRequest(proposed, current.IntegritySha256),
            CancellationToken.None);

        Assert.True(preview.CanApply, string.Join(" | ", preview.Errors.Concat(preview.Warnings)));
        Assert.NotEqual(current.IntegritySha256, preview.ProposedIntegritySha256);
        Assert.Contains(preview.ProfileDiffs, diff => diff.Changes.Contains("compiled typed plan changed"));
        Assert.Empty(preview.ProposedInventory.Unaccounted);

        var applied = await store.ApplyAsync(
            new GuidePackageUpdateRequest(proposed, current.IntegritySha256),
            CancellationToken.None);

        Assert.True(applied.IsActive);
        Assert.Equal(proposed.Version, applied.Package.Version);
        var active = await store.GetCurrentAsync(CancellationToken.None);
        Assert.Equal(applied.IntegritySha256, active.IntegritySha256);
        var versions = await store.ListAsync(CancellationToken.None);
        Assert.Single(versions);
        Assert.Equal(applied.IntegritySha256, versions[0].IntegritySha256);

        var repeated = await store.ApplyAsync(
            new GuidePackageUpdateRequest(proposed, applied.IntegritySha256),
            CancellationToken.None);
        Assert.Equal(applied.IntegritySha256, repeated.IntegritySha256);
        Assert.Single(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Preview_rejects_unaccounted_mappings_and_stale_activation_guards()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var store = new SqliteGuidePackageStore(storage.Factory, time);
        var current = await store.GetCurrentAsync(CancellationToken.None);
        var bad = current.Package with
        {
            Version = current.Package.Version + 1,
            CustomFormats = current.Package.CustomFormats
                .Select(format => format with
                {
                    MappingStatus = Deluno.Quality.Guides.GuideMappingStatus.Reviewed,
                    MappedTraitIds = ["trait.that.does.not.exist"]
                })
                .ToArray()
        };

        var preview = await store.PreviewAsync(
            new GuidePackageUpdateRequest(bad, current.IntegritySha256),
            CancellationToken.None);

        Assert.False(preview.CanApply);
        Assert.NotEmpty(preview.ProposedInventory.Unaccounted);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(
            new GuidePackageUpdateRequest(bad, current.IntegritySha256),
            CancellationToken.None));

        var valid = current.Package with { Version = current.Package.Version + 1 };
        await store.ApplyAsync(new GuidePackageUpdateRequest(valid, current.IntegritySha256), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(
            new GuidePackageUpdateRequest(valid, current.IntegritySha256),
            CancellationToken.None));
    }
}
