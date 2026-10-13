using Deluno.Api.Backup;
using Deluno.Api.Network;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.StateFiles;

/// <summary>
/// #1260: a state file that was cut short (its writer was killed) or is held by another process was read as "empty",
/// and the next change wrote that emptiness back over the real file. Each test here starts from a real file that is cut
/// short and asks that a change either leaves the file as it was or says it could not be made.
/// </summary>
public sealed class ACutShortStateFileIsNeverWrittenOverTests : IDisposable
{
    private const string CutShort = "{\"nzbget\":{\"Username\":\"deluno\",\"Pass";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-state-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Seed(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Saving_a_sign_in_never_erases_the_others_when_the_file_is_cut_short()
    {
        var path = Seed(FileBundledComponentCredentialStore.PathFor(_root), CutShort);
        var store = new FileBundledComponentCredentialStore(path, new ReversingProtector());

        try { store.Save("qbittorrent", new BundledComponentCredentials("deluno", "one")); } catch (IOException) { /* saying so is allowed */ }

        Assert.Equal(CutShort, File.ReadAllText(path));
    }

    [Fact]
    public void Removing_a_sign_in_never_erases_the_others_when_the_file_is_cut_short()
    {
        var path = Seed(FileBundledComponentCredentialStore.PathFor(_root), CutShort);
        var store = new FileBundledComponentCredentialStore(path, new ReversingProtector());

        try { store.Remove("qbittorrent"); } catch (IOException) { }

        Assert.Equal(CutShort, File.ReadAllText(path));
    }

    [Fact]
    public void Marking_an_app_installed_never_erases_the_others_when_the_file_is_cut_short()
    {
        const string cut = "[{\"Kind\":\"nzbget\",\"Host\":\"localh";
        var path = Seed(FileInstalledComponentRecord.PathFor(_root), cut);
        var record = new FileInstalledComponentRecord(path);

        try { record.MarkInstalled("qbittorrent", "localhost", 8081); } catch (IOException) { }

        Assert.Equal(cut, File.ReadAllText(path));
    }

    [Fact]
    public void Adding_a_pending_install_never_erases_the_others_when_the_file_is_cut_short()
    {
        const string cut = "{\"Kinds\":[\"nzbget\",\"we";
        var path = Seed(PendingComponentInstalls.PathFor(_root), cut);

        try { PendingComponentInstalls.Add(_root, ["weir"], null, askedByPerson: true, reason: "asked"); } catch (IOException) { }

        Assert.Equal(cut, File.ReadAllText(path));
    }

    [Fact]
    public async Task A_cut_short_backup_settings_file_does_not_stop_a_backup_for_good()
    {
        var storage = _root;
        Seed(Path.Combine(storage, "backups", "backup-settings.json"), "{\"enabled\":tr");
        File.WriteAllText(Path.Combine(storage, "platform.json"), "{}");
        var service = new DelunoBackupService(
            Options.Create(new StoragePathOptions { DataRoot = storage }),
            TimeProvider.System,
            NullLogger<DelunoBackupService>.Instance);

        var backup = await service.CreateBackupAsync("pre-update", CancellationToken.None);

        Assert.True(File.Exists(backup.FullPath));
    }

    [Fact]
    public void A_cut_short_maintenance_note_keeps_what_it_can_when_one_field_is_recorded()
    {
        const string cut = "{\"integrityCheckedUtc\":\"2026-10-01T00:00:00+00:00\",\"integ";
        var path = Seed(Path.Combine(_root, DatabaseMaintenanceLog.FileName), cut);

        DatabaseMaintenanceLog.RecordCompaction(_root, DateTimeOffset.Parse("2026-10-02T00:00:00Z"));

        Assert.Equal(cut, File.ReadAllText(path));
    }

    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string purpose, string plaintext) => "rev:" + purpose + ":" + new string(plaintext.Reverse().ToArray());

        public string? Unprotect(string purpose, string? protectedValue)
            => protectedValue is not null && protectedValue.StartsWith("rev:" + purpose + ":", StringComparison.Ordinal)
                ? new string(protectedValue[("rev:" + purpose + ":").Length..].Reverse().ToArray())
                : null;

        public bool IsProtected(string? value) => value?.StartsWith("rev:", StringComparison.Ordinal) == true;
    }
}
