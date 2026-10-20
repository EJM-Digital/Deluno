using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Deluno.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>#1361 part 7: the master key and protection keys travel in a backup only sealed with the backup password, on real files.</summary>
public sealed class BackupKeyPasswordTests
{
    private static DelunoBackupService Service(string root, TimeProvider clock)
        => new(Options.Create(new StoragePathOptions { DataRoot = root }), clock, NullLogger<DelunoBackupService>.Instance);

    private static void Seed(string root)
    {
        File.WriteAllText(Path.Combine(root, "platform.db"), "x");
        Directory.CreateDirectory(Path.Combine(root, "secrets"));
        File.WriteAllBytes(Path.Combine(root, "secrets", "master.key"), Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        Directory.CreateDirectory(Path.Combine(root, "protection-keys"));
        File.WriteAllText(Path.Combine(root, "protection-keys", "key-1.xml"), "<key>secret-material</key>");
    }

    private static async Task<string> BackupAsync(string root, string? password)
    {
        var service = Service(root, new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T01:00:00Z")));
        await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "03:00", 7, null, password), CancellationToken.None);
        var item = await service.CreateBackupAsync("manual", CancellationToken.None);
        return item.FullPath;
    }

    private static async Task<RestoreResultResponse> RestoreAsync(string target, string zip, string? password)
    {
        var service = Service(target, new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T02:00:00Z")));
        await using var stream = File.OpenRead(zip);
        return await service.RestoreAsync(stream, CancellationToken.None, backupPassword: password);
    }

    [Fact]
    public async Task With_the_right_password_the_keys_come_back_and_they_are_never_in_the_zip_in_the_clear()
    {
        using var source = new TempFolder();
        Seed(source);
        var zip = await BackupAsync(source, "correct horse");

        var bytes = await File.ReadAllBytesAsync(zip);
        Assert.DoesNotContain("secret-material", System.Text.Encoding.Latin1.GetString(bytes));
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("master.key") || e.FullName.Contains("protection-keys"));
            Assert.Contains(archive.Entries, e => e.FullName == BackupKeyVault.EntryName);
        }

        using var target = new TempFolder();
        var result = await RestoreAsync(target, zip, "correct horse");
        Assert.True(result.Restored, result.Message);
        StagedRestore.ApplyPending(target);
        Assert.Equal(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), await File.ReadAllBytesAsync(Path.Combine(target, "secrets", "master.key")));
        Assert.Equal("<key>secret-material</key>", await File.ReadAllTextAsync(Path.Combine(target, "protection-keys", "key-1.xml")));
    }

    [Fact]
    public async Task A_wrong_password_is_refused_in_plain_words_and_nothing_is_staged()
    {
        using var source = new TempFolder();
        Seed(source);
        var zip = await BackupAsync(source, "correct horse");

        using var target = new TempFolder();
        var result = await RestoreAsync(target, zip, "wrong horse");

        Assert.False(result.Restored);
        Assert.True(result.NeedsPassword);
        Assert.Contains("not the one this backup was made with", result.Message);
        Assert.False(Directory.Exists(Path.Combine(target, "restore-staging")) && Directory.GetFileSystemEntries(Path.Combine(target, "restore-staging")).Length > 0);
    }

    [Fact]
    public async Task A_backup_with_keys_asked_to_restore_with_no_password_asks_for_one()
    {
        using var source = new TempFolder();
        Seed(source);
        var zip = await BackupAsync(source, "correct horse");

        using var target = new TempFolder();
        var result = await RestoreAsync(target, zip, null);

        Assert.False(result.Restored);
        Assert.True(result.NeedsPassword);
        Assert.Contains("Enter that password", result.Message);
    }

    [Fact]
    public async Task A_backup_made_with_no_password_leaves_the_keys_out_says_so_and_restores_asking_for_the_secrets_again()
    {
        using var source = new TempFolder();
        Seed(source);
        var zip = await BackupAsync(source, null);

        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("master.key") || e.FullName.Contains("protection-keys") || e.FullName == BackupKeyVault.EntryName);
        }

        var service = Service(source, new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T02:00:00Z")));
        await using (var stream = File.OpenRead(zip))
        {
            var preview = await service.PreviewRestoreAsync(stream, CancellationToken.None);
            Assert.False(preview.NeedsPassword);
            Assert.Contains(preview.Warnings, w => w.Contains("keys are not in it"));
        }

        using var target = new TempFolder();
        var result = await RestoreAsync(target, zip, null);
        Assert.True(result.Restored, result.Message);
        Assert.True(result.SecretsNeedEntering);
        Assert.Contains("enter your saved passwords and API keys again", result.Message);
        StagedRestore.ApplyPending(target);
        Assert.False(File.Exists(Path.Combine(target, "secrets", "master.key")));
    }

    [Fact]
    public async Task The_password_is_never_shown_back_and_clearing_it_stops_keys_going_into_later_backups()
    {
        using var source = new TempFolder();
        Seed(source);
        var service = Service(source, new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T01:00:00Z")));
        var saved = await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "03:00", 7, null, "pw one"), CancellationToken.None);
        Assert.True(saved.PasswordSet);
        Assert.DoesNotContain("pw one", System.Text.Json.JsonSerializer.Serialize(saved));

        var cleared = await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "03:00", 7, null, null, ClearBackupPassword: true), CancellationToken.None);
        Assert.False(cleared.PasswordSet);
        var item = await service.CreateBackupAsync("manual", CancellationToken.None);
        Assert.False(item.KeysIncluded);
    }

    private static DelunoBackupService ProtectedService(string root, ISecretProtector protector)
        => new(Options.Create(new StoragePathOptions { DataRoot = root }), new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T01:00:00Z")), NullLogger<DelunoBackupService>.Instance, secretProtector: protector);

    private static bool ZipHoldsKeys(string zip)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        return archive.Entries.Any(e => e.FullName == BackupKeyVault.EntryName);
    }

    [Fact]
    public async Task With_a_protector_the_saved_password_is_stored_sealed_and_still_seals_the_keys()
    {
        using var source = new TempFolder();
        Seed(source);
        var service = ProtectedService(source, new ReversingProtector());
        await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "03:00", 7, null, "correct horse"), CancellationToken.None);

        var settingsFile = Directory.GetFiles(source, "backup-settings.json", SearchOption.AllDirectories).Single();
        var onDisk = await File.ReadAllTextAsync(settingsFile);
        Assert.DoesNotContain("correct horse", onDisk);
        Assert.Contains("sealed:", onDisk);

        var item = await service.CreateBackupAsync("manual", CancellationToken.None);
        Assert.True(item.KeysIncluded);
        Assert.True(ZipHoldsKeys(item.FullPath));

        using var target = new TempFolder();
        var result = await RestoreAsync(target, item.FullPath, "correct horse");
        Assert.True(result.Restored, result.Message);
    }

    [Fact]
    public async Task A_password_saved_before_a_protector_existed_is_still_read_so_keys_keep_going_in()
    {
        using var source = new TempFolder();
        Seed(source);
        await Service(source, new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T01:00:00Z")))
            .SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "03:00", 7, null, "old pw"), CancellationToken.None);

        var item = await ProtectedService(source, new ReversingProtector()).CreateBackupAsync("manual", CancellationToken.None);

        Assert.True(item.KeysIncluded);
        Assert.True(ZipHoldsKeys(item.FullPath));
    }

    [Fact]
    public async Task A_saved_password_that_cannot_be_read_here_leaves_the_keys_out_instead_of_failing_the_backup()
    {
        using var source = new TempFolder();
        Seed(source);
        var service = ProtectedService(source, new ReversingProtector { CannotRead = true });
        await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "03:00", 7, null, "pw"), CancellationToken.None);

        var item = await service.CreateBackupAsync("manual", CancellationToken.None);

        Assert.False(item.KeysIncluded);
        Assert.False(ZipHoldsKeys(item.FullPath));
    }

    private sealed class ReversingProtector : ISecretProtector
    {
        public bool CannotRead { get; init; }

        public string Protect(string purpose, string plaintext) => "sealed:" + new string(plaintext.Reverse().ToArray());

        public string? Unprotect(string purpose, string? protectedValue)
            => CannotRead || protectedValue is null || !protectedValue.StartsWith("sealed:", StringComparison.Ordinal)
                ? null
                : new string(protectedValue["sealed:".Length..].Reverse().ToArray());

        public bool IsProtected(string? value) => value?.StartsWith("sealed:", StringComparison.Ordinal) == true;
    }

    private sealed class TempFolder : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "deluno-backup-key-tests", Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(path);

        public static implicit operator string(TempFolder scope) => scope.path;

        public void Dispose()
        {
            try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
