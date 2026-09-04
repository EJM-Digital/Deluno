using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Deluno.Infrastructure.Storage;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #351 line 6: rollback/restore is exercised on a populated database.
///
/// <para>The existing restore coverage moves text files between two data
/// roots, which proves the archive round-trips and nothing about SQLite. A
/// migration takes its verified backup while real databases are open, with
/// their write-ahead logs holding rows that are not in the .db file yet — so
/// the question this answers is whether the rows come back, read through the
/// repositories, not whether the bytes come back.</para>
///
/// <para>It is a drill rather than a unit test on purpose: create the state,
/// back it up the way migration does, change it the way migration does, put it
/// back, and look.</para>
/// </summary>
public sealed class MigrationRestoreDrillTests
{
    [Fact]
    public async Task A_verified_migration_backup_restores_the_profiles_and_formats_it_was_taken_of()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-03T04:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var quality = new SqliteQualityRepository(storage.Factory, clock);

        var format = await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest("Prefer original audio", "movies", 250, "trash-original-audio", "audio=original", true),
            CancellationToken.None);
        var profile = await quality.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "Before migration",
                "movies",
                "Bluray 1080p",
                "WEB 1080p, Bluray 1080p",
                format.Id,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false),
            CancellationToken.None);

        // The backup copies files off disk, so the write-ahead log has to have
        // reached them. This is what a running host does when it closes its
        // pooled connections; skipping it is how a "successful" backup comes
        // back empty.
        SqliteConnection.ClearAllPools();

        var backupService = new DelunoBackupService(
            Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }),
            clock,
            NullLogger<DelunoBackupService>.Instance);

        // The exact call the migration path makes before its first write.
        var receipt = await backupService.CreateVerifiedBackupAsync("pre-migration", CancellationToken.None);
        Assert.Equal("manifest-and-restore-preview-verified", receipt.Verification);
        Assert.True(receipt.SizeBytes > 0);

        // Now do what a migration does: change the profile, and delete the
        // format it referenced. This is the shape of the accident rollback
        // exists for — a reference that no longer resolves.
        await quality.UpdateQualityProfileAsync(
            profile.Id,
            new UpdateQualityProfileRequest(
                "After migration",
                "WEB 720p",
                "WEB 720p",
                string.Empty,
                UpgradeUntilCutoff: false,
                UpgradeUnknownItems: true),
            CancellationToken.None);
        Assert.True(await quality.DeleteCustomFormatAsync(format.Id, CancellationToken.None));

        var changed = Assert.Single(await quality.ListQualityProfilesAsync(CancellationToken.None));
        Assert.Equal("After migration", changed.Name);
        Assert.Empty(await quality.ListCustomFormatsAsync(CancellationToken.None));

        // Roll back.
        SqliteConnection.ClearAllPools();
        var opened = await backupService.OpenBackupAsync(receipt.BackupId, CancellationToken.None);
        Assert.NotNull(opened);
        RestoreResultResponse restored;
        await using (var stream = opened!.Value.Stream)
        {
            restored = await backupService.RestoreAsync(stream, CancellationToken.None);
        }

        // RestoreAsync stages; applying is what a restart does. This drill has
        // already released every pooled connection above, which is the same
        // condition a restart creates, so it applies here.
        StagedRestore.ApplyPending(storage.DataRoot);
        {
        }

        Assert.True(restored.Restored);
        Assert.Contains(restored.RestoredFiles, file => file.EndsWith("platform.db", StringComparison.OrdinalIgnoreCase));

        // Read it back through the repositories rather than off the disk: a
        // restore that produces a file SQLite will not open is not a restore.
        SqliteConnection.ClearAllPools();
        var afterRestore = new SqliteQualityRepository(storage.Factory, clock);

        var recoveredProfile = Assert.Single(await afterRestore.ListQualityProfilesAsync(CancellationToken.None));
        Assert.Equal(profile.Id, recoveredProfile.Id);
        Assert.Equal("Before migration", recoveredProfile.Name);
        Assert.Equal("Bluray 1080p", recoveredProfile.CutoffQuality);
        Assert.Equal("WEB 1080p, Bluray 1080p", recoveredProfile.AllowedQualities);
        Assert.True(recoveredProfile.UpgradeUntilCutoff);
        Assert.False(recoveredProfile.UpgradeUnknownItems);

        // And the reference resolves again, which is the whole point: a
        // profile whose format is gone is the dangling state rollback undoes.
        Assert.Equal(format.Id, recoveredProfile.CustomFormatIds);
        var recoveredFormat = Assert.Single(await afterRestore.ListCustomFormatsAsync(CancellationToken.None));
        Assert.Equal(format.Id, recoveredFormat.Id);
        Assert.Equal("Prefer original audio", recoveredFormat.Name);
        Assert.Equal(250, recoveredFormat.Score);
        Assert.Equal("trash-original-audio", recoveredFormat.TrashId);
        Assert.Equal("audio=original", recoveredFormat.Conditions);

        // The state that was replaced is kept beside it, so a rollback taken
        // by mistake is itself recoverable.
        Assert.True(File.Exists(Path.Combine(storage.DataRoot, "platform.db.pre-restore")));
    }
}
