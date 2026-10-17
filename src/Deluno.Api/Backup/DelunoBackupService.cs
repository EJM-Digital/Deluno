using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Platform.Migration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Api.Backup;

public interface IDelunoBackupService
{
    Task<BackupSettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken);
    Task<BackupSettingsSnapshot> SaveSettingsAsync(UpdateBackupSettingsRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupItem>> ListBackupsAsync(CancellationToken cancellationToken);
    Task<BackupItem> CreateBackupAsync(string reason, CancellationToken cancellationToken);
    Task<(Stream Stream, string ContentType, string FileName)?> OpenBackupAsync(string id, CancellationToken cancellationToken);
    Task<bool> DeleteBackupAsync(string id, CancellationToken cancellationToken);
    Task<RestorePreviewResponse> PreviewRestoreAsync(Stream backupStream, CancellationToken cancellationToken);
    Task<RestoreResultResponse> RestoreAsync(Stream backupStream, CancellationToken cancellationToken, bool confirmNewerVersion = false);
}

public sealed class DelunoBackupService(
    IOptions<StoragePathOptions> storageOptions,
    TimeProvider timeProvider,
    ILogger<DelunoBackupService> logger,
    IJobQueueRepository? jobQueueRepository = null,
    IRealtimeEventPublisher? realtime = null,
    IStateFileSystem? stateFiles = null)
    : BackgroundService, IDelunoBackupService, IMigrationBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    private string DataRoot => Path.GetFullPath(storageOptions.Value.DataRoot);
    private string DefaultBackupFolder => Path.Combine(DataRoot, "backups");
    private string SettingsPath => Path.Combine(DefaultBackupFolder, "backup-settings.json");

    public async Task<BackupSettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var state = await ReadSettingsStateAsync(cancellationToken);
        return ToSnapshot(state);
    }

    public async Task<BackupSettingsSnapshot> SaveSettingsAsync(UpdateBackupSettingsRequest request, CancellationToken cancellationToken)
    {
        // Under the backup gate (#1260): a backup that is running read the settings before this change and writes its run times
        // when it ends, and must never put the old settings back. This waits for it, then changes the file as it now is.
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadSettingsStateAsync(cancellationToken);
            if (state.Kind == StateFileKind.Busy)
            {
                // Nothing is known about what is saved, so a change could undo part of it: say so rather than guess.
                throw new PlainFailureException(state.Problem ?? "The backup settings are in use by something else right now. Try again in a moment.");
            }

            state.Enabled = request.Enabled;
            state.Frequency = NormalizeFrequency(request.Frequency);
            state.TimeOfDay = NormalizeTimeOfDay(request.TimeOfDay);
            state.RetentionCount = Math.Clamp(request.RetentionCount ?? state.RetentionCount, 1, 10000);
            state.BackupFolder = NormalizeBackupFolder(request.BackupFolder);
            state.NextRunUtc = CalculateNextRun(state, timeProvider.GetUtcNow());
            // A damaged file is replaced here on purpose: the person has just chosen new settings, and a copy of the old one was kept.
            state.Kind = StateFileKind.Ok;
            state.Problem = null;
            await WriteSettingsStateAsync(state, cancellationToken);
            await realtime.PublishBackupChangedAsync(cancellationToken);
            return ToSnapshot(state);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<BackupItem>> ListBackupsAsync(CancellationToken cancellationToken)
    {
        var state = await ReadSettingsStateAsync(cancellationToken);
        Directory.CreateDirectory(EffectiveBackupFolder(state));

        return Directory
            .EnumerateFiles(EffectiveBackupFolder(state), "*.zip", SearchOption.TopDirectoryOnly)
            .Select(ReadBackupItem)
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.CreatedUtc)
            .ToArray();
    }

    public Task<BackupItem> CreateBackupAsync(string reason, CancellationToken cancellationToken)
        => CreateBackupAsync(reason, cancellationToken, onFile: null);

    /// <summary>
    /// Takes the backup, saying before each file which one it is about to copy ("3 of 9: movies.db"), so a start that
    /// waits for the backup can show how far it has got and tell a slow backup from a stuck one (#1256).
    /// </summary>
    public async Task<BackupItem> CreateBackupAsync(string reason, CancellationToken cancellationToken, Action<string>? onFile)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadSettingsStateAsync(cancellationToken);
            Directory.CreateDirectory(EffectiveBackupFolder(state));

            var now = timeProvider.GetUtcNow();
            var safeReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason.Trim();
            var id = $"deluno-backup-{now:yyyyMMdd-HHmmss}";
            var targetPath = Path.Combine(EffectiveBackupFolder(state), $"{id}.zip");
            var dataFiles = EnumerateDataFiles().ToArray();
            var archived = new List<string>(dataFiles.Length);
            var snapshotFolder = Path.Combine(DataRoot, SnapshotFolderName, Guid.NewGuid().ToString("N"));

            try
            {
            using (var fileStream = File.Create(targetPath))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                var position = 0;
                foreach (var file in dataFiles)
                {
                    // Before the file is copied: a start waiting on this backup shows how far it has got.
                    onFile?.Invoke($"{++position} of {dataFiles.Length}: {Path.GetFileName(file)}");
                    // Opened first, because opening is the only honest test of
                    // whether the file is still there. A `-wal` belongs to a
                    // database that is still being served, and SQLite deletes it
                    // the moment the last connection to that database closes —
                    // which can happen between the listing above and this line.
                    // Asking a file that is gone for its timestamp does not
                    // throw: it answers 1601-01-01, which a zip cannot store,
                    // and the whole backup then died on an unhandled exception.
                    FileStream sourceStream;
                    try
                    {
                        // A database is snapshotted through SQLite itself, so
                        // the copy is one consistent moment and never a file
                        // (or its -wal) caught half-written. Anything else is
                        // copied as it is.
                        sourceStream = IsSqliteDatabase(file)
                            ? SnapshotDatabase(file, snapshotFolder)
                            : new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (Exception exception) when (exception is FileNotFoundException
                                                          or DirectoryNotFoundException
                                                          or IOException
                                                          or SqliteException
                                                          or UnauthorizedAccessException)
                    {
                        // One file Deluno could not read is not a reason to have
                        // no backup at all. A vanished `-wal` costs nothing in
                        // particular: SQLite removed it because it had already
                        // checkpointed what it held into the database file this
                        // is copying anyway.
                        logger.LogWarning(
                            exception,
                            "Left {File} out of the backup because it could not be read.",
                            file);
                        continue;
                    }

                    using (sourceStream)
                    {
                        var relativePath = Path.GetRelativePath(DataRoot, file).Replace('\\', '/');
                        var entry = archive.CreateEntry($"data/{relativePath}", CompressionLevel.Optimal);
                        entry.LastWriteTime = ZipSafeTimestamp(File.GetLastWriteTime(file));
                        using var entryStream = entry.Open();
                        sourceStream.CopyTo(entryStream);
                        archived.Add(Path.GetRelativePath(DataRoot, file));
                    }
                }

                // Written last, and from what actually went in, so the manifest
                // is a record rather than an intention.
                var manifest = new BackupManifest(
                    App: "Deluno",
                    Version: GetVersion(),
                    CreatedUtc: now,
                    Reason: safeReason,
                    Files: archived.Order(StringComparer.OrdinalIgnoreCase).ToArray());

                var manifestEntry = archive.CreateEntry("deluno-backup.json", CompressionLevel.Optimal);
                await using (var manifestStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken);
                }
            }
            }
            finally
            {
                TryDeleteFolder(snapshotFolder);
                TryDeleteFolder(Path.Combine(DataRoot, SnapshotFolderName), onlyIfEmpty: true);
            }

            RecordRun(state, now);
            await EnforceRetentionAsync(state, cancellationToken);

            logger.LogInformation("Created Deluno backup {BackupPath}.", targetPath);
            await realtime.PublishBackupChangedAsync(cancellationToken);
            return ReadBackupItem(targetPath) ?? throw new InvalidOperationException("Backup was created but could not be read.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<(Stream Stream, string ContentType, string FileName)?> OpenBackupAsync(string id, CancellationToken cancellationToken)
    {
        var state = await ReadSettingsStateAsync(cancellationToken);
        var file = ResolveBackupPath(EffectiveBackupFolder(state), id);
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return (stream, "application/zip", Path.GetFileName(file));
    }

    public async Task<bool> DeleteBackupAsync(string id, CancellationToken cancellationToken)
    {
        var state = await ReadSettingsStateAsync(cancellationToken);
        var file = ResolveBackupPath(EffectiveBackupFolder(state), id);
        if (file is null || !File.Exists(file))
        {
            return false;
        }

        File.Delete(file);
        await realtime.PublishBackupChangedAsync(cancellationToken);
        return true;
    }

    public async Task<RestorePreviewResponse> PreviewRestoreAsync(Stream backupStream, CancellationToken cancellationToken)
    {
        await using var buffered = new MemoryStream();
        await backupStream.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;
        return await PreviewRestoreCoreAsync(buffered, cancellationToken);
    }

    public async Task<RestoreResultResponse> RestoreAsync(Stream backupStream, CancellationToken cancellationToken, bool confirmNewerVersion = false)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var buffered = new MemoryStream();
            await backupStream.CopyToAsync(buffered, cancellationToken);
            buffered.Position = 0;
            var preview = await PreviewRestoreCoreAsync(buffered, cancellationToken);
            if (!preview.Valid)
            {
                return new RestoreResultResponse(false, preview.Message, string.Empty, []);
            }

            // #1349: a backup from a newer Deluno may hold data this version cannot read. It is restored only when the person
            // has confirmed it after reading the warning.
            if (preview.NewerThanThisVersion && !confirmNewerVersion)
            {
                return new RestoreResultResponse(false, NewerBackupMessage(preview.Manifest?.Version), string.Empty, []);
            }

            buffered.Position = 0;

            // Staged, not written straight over the live files.
            //
            // Restoring used to extract each database directly onto the running
            // application's open SQLite files. On Windows that throws on the
            // very first one - confirmed on an installed build, where the only
            // trace left behind was a single cache.db.pre-restore copy and a
            // 500 - so the documented recovery path did not work at all.
            //
            // The staging folder was already being created here and never
            // written to, which is the shape of an intention that was not
            // finished. It is finished now: the archive lands in staging, and
            // StagedRestore applies it on the next start before anything opens
            // a database. Restarting is not "recommended" any more, it is how
            // the restore completes.
            var restoreRoot = Path.Combine(
                DataRoot,
                StagedRestore.StagingFolderName,
                timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(restoreRoot);
            var restored = new List<string>();

            using (var archive = new ZipArchive(buffered, ZipArchiveMode.Read, leaveOpen: true))
            {
                foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("data/", StringComparison.OrdinalIgnoreCase)))
                {
                    var relative = entry.FullName["data/".Length..].Replace('/', Path.DirectorySeparatorChar);
                    if (string.IsNullOrWhiteSpace(relative))
                    {
                        continue;
                    }

                    // Checked against the live root rather than the staging
                    // folder: what matters is where this file will eventually
                    // land, and a traversal that escapes there escapes whether
                    // it is staged first or not.
                    if (!Deluno.Contracts.SafePath.TryResolveUnder(DataRoot, relative, out var resolved, out _))
                    {
                        continue;
                    }

                    var eventualTarget = Path.GetFullPath(Path.Combine(DataRoot, relative));

                    var staged = Path.Combine(restoreRoot, Path.GetRelativePath(DataRoot, resolved));
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    entry.ExtractToFile(staged, overwrite: true);
                    restored.Add(Path.GetRelativePath(DataRoot, eventualTarget));
                }
            }

            if (restored.Count == 0)
            {
                Directory.Delete(restoreRoot, recursive: true);
                return new RestoreResultResponse(false, "No restorable data files were found.", string.Empty, []);
            }

            StagedRestore.Arm(DataRoot, restoreRoot);
            logger.LogWarning(
                "Staged {Count} file(s) from an uploaded backup. They are applied on the next start.",
                restored.Count);

            return new RestoreResultResponse(
                Restored: true,
                Message: "Backup staged. Restart Deluno to apply it — the files are written before any database is opened.",
                RestoreFolder: restoreRoot,
                RestoredFiles: restored);
        }
        finally
        {
            gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var state = await ReadSettingsStateAsync(stoppingToken);
                if (state.Enabled && state.NextRunUtc is { } nextRun && nextRun <= timeProvider.GetUtcNow())
                {
                    if (jobQueueRepository is null || await jobQueueRepository.TryClaimScheduledPassAsync(
                            SystemTasks.Backup,
                            SystemTasks.IntervalFor(SystemTasks.Backup),
                            stoppingToken))
                    {
                        var startedUtc = timeProvider.GetUtcNow();
                        try
                        {
                            await CreateBackupAsync("scheduled", stoppingToken);
                            var next = (await GetSettingsAsync(stoppingToken)).NextRunUtc;
                            await RecordScheduledPassOutcomeAsync(
                                startedUtc,
                                "completed",
                                next,
                                stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            await RecordScheduledPassOutcomeAsync(startedUtc, "cancelled", null, CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            await RecordScheduledPassOutcomeAsync(startedUtc, "failed", null, CancellationToken.None, PlainFailure.Describe(exception, "making the scheduled backup"));
                            logger.LogWarning(exception, "Scheduled backup creation failed.");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scheduled backup check failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task<RestorePreviewResponse> PreviewRestoreCoreAsync(Stream backupStream, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = new ZipArchive(backupStream, ZipArchiveMode.Read, leaveOpen: true);
            var manifestEntry = archive.GetEntry("deluno-backup.json");
            if (manifestEntry is null)
            {
                return new RestorePreviewResponse(false, "This file is not a Deluno backup.", null, []);
            }

            await using var stream = manifestEntry.Open();
            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken);
            if (manifest is null || !string.Equals(manifest.App, "Deluno", StringComparison.OrdinalIgnoreCase))
            {
                return new RestorePreviewResponse(false, "The backup manifest is invalid.", manifest, []);
            }

            var warnings = new List<string>();
            if (!archive.Entries.Any(entry => entry.FullName.StartsWith("data/", StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add("The archive does not contain any data files.");
            }

            var newer = DelunoVersion.IsNewer(manifest.Version, GetVersion());
            if (newer)
            {
                warnings.Add(NewerBackupMessage(manifest.Version));
            }

            return new RestorePreviewResponse(true, "Backup can be restored.", manifest, warnings, newer);
        }
        catch (Exception ex)
        {
            return new RestorePreviewResponse(false, ex.Message, null, []);
        }
    }

    /// <summary>
    /// A moment a zip file can actually hold.
    ///
    /// <para>Zip stores timestamps in MS-DOS form, which begins in 1980 and ends
    /// in 2107. Outside that <see cref="System.IO.Compression.ZipArchiveEntry.LastWriteTime"/>
    /// throws, and it is the caller's whole operation that fails, not the one
    /// entry.</para>
    ///
    /// <para>Two things land outside it. A file that has been deleted answers
    /// 1601-01-01 rather than failing, which is how a vanished <c>-wal</c> took
    /// a backup down. And a genuinely old timestamp — a file restored from an
    /// archive, or written while the clock was wrong — is outside it for real.
    /// Neither is a reason to refuse to back the data up, so the timestamp is
    /// clamped and the contents are kept.</para>
    /// </summary>
    private static DateTimeOffset ZipSafeTimestamp(DateTime lastWriteTime)
    {
        if (lastWriteTime < ZipFirstMoment)
        {
            return new DateTimeOffset(ZipFirstMoment);
        }

        return lastWriteTime > ZipLastMoment
            ? new DateTimeOffset(ZipLastMoment)
            : new DateTimeOffset(lastWriteTime);
    }

    private static readonly DateTime ZipFirstMoment = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
    private static readonly DateTime ZipLastMoment = new(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);

    private const string SnapshotFolderName = "backup-snapshots";

    private static bool IsSqliteDatabase(string file)
    {
        if (!file.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[16];
            return stream.Read(header) == 16
                && header.SequenceEqual("SQLite format 3\0"u8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies one database with SQLite's own online-backup API into the
    /// snapshot folder and opens the copy. The copy is a whole, checkpointed
    /// database with no -wal beside it, taken while Deluno keeps writing.
    /// </summary>
    private static FileStream SnapshotDatabase(string file, string snapshotFolder)
    {
        Directory.CreateDirectory(snapshotFolder);
        var target = Path.Combine(snapshotFolder, Guid.NewGuid().ToString("N") + ".db");

        var sourceString = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
        var targetString = new SqliteConnectionStringBuilder
        {
            DataSource = target,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using (var source = new SqliteConnection(sourceString))
        using (var destination = new SqliteConnection(targetString))
        {
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
            using var journal = destination.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode = DELETE;";
            journal.ExecuteNonQuery();
        }

        return new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
    }

    private static void TryDeleteFolder(string folder, bool onlyIfEmpty = false)
    {
        try
        {
            if (Directory.Exists(folder) && (!onlyIfEmpty || !Directory.EnumerateFileSystemEntries(folder).Any()))
            {
                Directory.Delete(folder, recursive: !onlyIfEmpty);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private IEnumerable<string> EnumerateDataFiles()
    {
        Directory.CreateDirectory(DataRoot);
        var excludedRoots = new[]
        {
            Path.Combine(DataRoot, "backups"),
            Path.Combine(DataRoot, "restore-staging"),
            Path.Combine(DataRoot, SnapshotFolderName),
            // The application log (DelunoFileLoggerProvider). Weeks of lines
            // would swell every backup, and a restore would write an old log
            // back over the one that explains why the restore was needed.
            Path.Combine(DataRoot, "logs")
        };

        return Directory
            .EnumerateFiles(DataRoot, "*", SearchOption.AllDirectories)
            .Where(file => excludedRoots.All(excluded => !Path.GetFullPath(file).StartsWith(Path.GetFullPath(excluded), StringComparison.OrdinalIgnoreCase)))
            .Where(file => !file.EndsWith(".pre-restore", StringComparison.OrdinalIgnoreCase))
            // The write-ahead log and its index belong to the live connection.
            // The database is snapshotted whole, so neither goes in.
            .Where(file => !file.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase));
    }

    private BackupItem? ReadBackupItem(string file)
    {
        try
        {
            var info = new FileInfo(file);
            var reason = "manual";
            var createdUtc = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
            using var archive = ZipFile.OpenRead(file);
            var manifestEntry = archive.GetEntry("deluno-backup.json");
            if (manifestEntry is not null)
            {
                using var stream = manifestEntry.Open();
                var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, JsonOptions);
                if (manifest is not null)
                {
                    reason = manifest.Reason;
                    createdUtc = manifest.CreatedUtc;
                }
            }

            return new BackupItem(
                Id: Path.GetFileNameWithoutExtension(file),
                FileName: Path.GetFileName(file),
                FullPath: file,
                SizeBytes: info.Length,
                CreatedUtc: createdUtc,
                Reason: reason);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read backup manifest from {BackupPath}.", file);
            return null;
        }
    }

    private string? otherFilesNote;

    /// <summary>
    /// Removes the oldest backups past the retention count, and only backups Deluno recorded making: an archive whose own
    /// manifest says it is a Deluno backup. Any other file in the folder (an owner's zip, a download) is left alone and
    /// named once in plain words in the backup settings' problem line and the log.
    /// </summary>
    private async Task EnforceRetentionAsync(BackupSettingsState state, CancellationToken cancellationToken)
    {
        var folder = EffectiveBackupFolder(state);
        var items = await ListBackupsAsync(cancellationToken);
        var recorded = items.Where(item => HasDelunoManifest(item.FullPath)).ToArray();
        foreach (var item in recorded.Skip(state.RetentionCount))
        {
            File.Delete(item.FullPath);
        }

        var others = Directory
            .EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null
                && !name.StartsWith("backup-settings.json", StringComparison.OrdinalIgnoreCase)
                && !recorded.Any(item => string.Equals(item.FileName, name, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var note = others.Length == 0
            ? null
            : $"The backup folder also holds {others.Length} file{(others.Length == 1 ? string.Empty : "s")} Deluno did not make ({string.Join(", ", others)}). Deluno leaves {(others.Length == 1 ? "it" : "them")} alone and never removes {(others.Length == 1 ? "it" : "them")} to tidy old backups.";
        if (note is not null && !string.Equals(note, otherFilesNote, StringComparison.Ordinal))
        {
            logger.LogInformation("{Note}", note);
        }

        otherFilesNote = note;
    }

    private bool HasDelunoManifest(string file)
    {
        try
        {
            using var archive = ZipFile.OpenRead(file);
            var entry = archive.GetEntry("deluno-backup.json");
            if (entry is null)
            {
                return false;
            }

            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, JsonOptions);
            return manifest is not null && string.Equals(manifest.App, "Deluno", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The backup settings. A missing file is a first run and is written with the standard settings. A file that is busy, or
    /// damaged, gives the standard settings for this call only, with <see cref="BackupSettingsState.Problem"/> in everyday words:
    /// a backup can still be taken, and nothing writes those standard settings over the file (#1260).
    /// </summary>
    private async Task<BackupSettingsState> ReadSettingsStateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DefaultBackupFolder);
        var read = StateFile.ReadLocked<BackupSettingsState>(SettingsPath, JsonOptions, stateFiles);
        switch (read.Kind)
        {
            case StateFileKind.Missing:
                var initial = StandardSettings();
                // Deliberately empty. Writing the computed default here turned
                // "wherever the data root is" into a fixed absolute path the
                // moment the file was first created - so a data directory
                // copied, restored or moved to another machine kept sending
                // backups to the old machine's path. This rig inherited
                // C:\Projects\Deluno\src\Deluno.Host\dataackups from a
                // developer's checkout and dutifully created it on a server.
                // Empty means "the default, wherever that is now".
                await WriteSettingsStateAsync(initial, cancellationToken);
                return initial;
            case StateFileKind.Busy:
                {
                    var standard = StandardSettings();
                    standard.Kind = StateFileKind.Busy;
                    standard.Problem = "The backup settings were in use by something else just now, so the standard settings were used for this backup. Nothing was changed.";
                    logger.LogWarning("The backup settings file was busy ({Detail}); the standard settings were used and nothing was written.", read.Detail);
                    return standard;
                }

            case StateFileKind.Unreadable:
                {
                    var kept = StateFile.KeepUnreadableCopy(SettingsPath, stateFiles);
                    var standard = StandardSettings();
                    standard.Kind = StateFileKind.Unreadable;
                    standard.Problem = $"The backup settings file is damaged, so the standard settings are in use and scheduled backups are off until you save the settings again.{(kept is null ? string.Empty : $" A copy of the damaged file was kept as {Path.GetFileName(kept)}.")}";
                    logger.LogWarning("The backup settings file {Path} is damaged ({Detail}); the standard settings are in use. {Copy}", SettingsPath, read.Detail, kept);
                    return standard;
                }
        }

        var state = read.Value!;
        state.Frequency = NormalizeFrequency(state.Frequency);
        state.TimeOfDay = NormalizeTimeOfDay(state.TimeOfDay);
        state.RetentionCount = Math.Clamp(state.RetentionCount, 1, 10000);
        state.BackupFolder = NormalizeBackupFolder(state.BackupFolder);
        if (state.Enabled && state.NextRunUtc is null)
        {
            state.NextRunUtc = CalculateNextRun(state, timeProvider.GetUtcNow());
            await WriteSettingsStateAsync(state, cancellationToken);
        }

        return state;
    }

    private static BackupSettingsState StandardSettings()
        => new()
        {
            Enabled = false,
            Frequency = "daily",
            TimeOfDay = "03:00",
            RetentionCount = 7,
            BackupFolder = string.Empty,
            LastRunUtc = null,
            NextRunUtc = null
        };

    /// <summary>
    /// Writes the settings atomically. Nothing is written for settings that stand in for a file that could not be read
    /// (<see cref="BackupSettingsState.Kind"/> is Busy or Unreadable): that is how a good file was turned into defaults.
    /// </summary>
    private Task WriteSettingsStateAsync(BackupSettingsState state, CancellationToken cancellationToken)
    {
        if (state.Kind is StateFileKind.Busy or StateFileKind.Unreadable)
        {
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(DefaultBackupFolder);
        Directory.CreateDirectory(EffectiveBackupFolder(state));
        if (StateFile.WriteJson(SettingsPath, state, JsonOptions, stateFiles) is { } problem)
        {
            throw new StateFileException($"The backup settings could not be saved: {problem}.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A backup ended: record when, by reading the settings as they are now and changing only the run times, so a change made
    /// while the backup ran is kept (#1260). A note that cannot be kept does not undo a backup that was taken; it is logged.
    /// </summary>
    private void RecordRun(BackupSettingsState state, DateTimeOffset now)
    {
        state.LastRunUtc = now;
        state.NextRunUtc = CalculateNextRun(state, now);
        if (state.Kind is StateFileKind.Busy or StateFileKind.Unreadable)
        {
            return;
        }

        var result = StateFile.Update<BackupSettingsState>(
            SettingsPath,
            current =>
            {
                var latest = current ?? state;
                latest.LastRunUtc = now;
                latest.NextRunUtc = CalculateNextRun(latest, now);
                return latest;
            },
            "The backup settings",
            JsonOptions,
            stateFiles);
        if (result.Problem is not null)
        {
            logger.LogWarning("The backup was taken, but when it ran was not recorded: {Reason}", result.Problem);
        }
    }

    private BackupSettingsSnapshot ToSnapshot(BackupSettingsState state)
        => new(
            state.Enabled,
            state.Frequency,
            state.TimeOfDay,
            state.RetentionCount,
            EffectiveBackupFolder(state),
            state.LastRunUtc,
            state.NextRunUtc,
            state.Problem ?? otherFilesNote);

    private DateTimeOffset? CalculateNextRun(BackupSettingsState state, DateTimeOffset now)
    {
        if (!state.Enabled)
        {
            return null;
        }

        var time = TimeSpan.TryParseExact(state.TimeOfDay, "hh\\:mm", CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : TimeSpan.FromHours(3);
        var candidate = new DateTimeOffset(now.Date + time, TimeSpan.Zero);
        while (candidate <= now)
        {
            candidate = state.Frequency switch
            {
                "weekly" => candidate.AddDays(7),
                "monthly" => candidate.AddMonths(1),
                _ => candidate.AddDays(1)
            };
        }

        return candidate;
    }

    /// <summary>
    /// What the owner chose, or nothing. An empty answer means "the default",
    /// which is resolved from the data root at the moment it is used rather
    /// than frozen into the settings file.
    /// </summary>
    private static string NormalizeBackupFolder(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value.Trim());

    /// <summary>
    /// The folder to actually write to: the chosen one, else today's default.
    /// </summary>
    private string EffectiveBackupFolder(BackupSettingsState state)
        => string.IsNullOrWhiteSpace(state.BackupFolder) ? DefaultBackupFolder : state.BackupFolder;

    private static string NormalizeFrequency(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "weekly" => "weekly",
            "monthly" => "monthly",
            _ => "daily"
        };

    private static string NormalizeTimeOfDay(string? value)
        => TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out var time)
            ? time.ToString("hh\\:mm", CultureInfo.InvariantCulture)
            : "03:00";

    private static string? ResolveBackupPath(string backupFolder, string id)
    {
        var safeId = Path.GetFileNameWithoutExtension(id);
        if (string.IsNullOrWhiteSpace(safeId))
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(backupFolder, $"{safeId}.zip"));
        return Deluno.Contracts.SafePath.IsInside(Path.GetFullPath(backupFolder), path) ? path : null;
    }

    private static string NewerBackupMessage(string? backupVersion)
        => $"This backup was made by a newer Deluno ({backupVersion}) than the one running now ({GetVersion()}). It may hold data this version cannot read. Update Deluno first, or confirm that you want to restore it anyway.";

    private static string GetVersion()
        => DelunoVersion.Running;

    private async Task RecordScheduledPassOutcomeAsync(
        DateTimeOffset startedUtc,
        string result,
        DateTimeOffset? nextRunUtc,
        CancellationToken cancellationToken,
        string? failureReason = null)
    {
        if (jobQueueRepository is null)
        {
            return;
        }

        var completedUtc = timeProvider.GetUtcNow();
        await jobQueueRepository.RecordScheduledPassOutcomeAsync(
            SystemTasks.Backup,
            completedUtc,
            result,
            Math.Max(0, (long)(completedUtc - startedUtc).TotalMilliseconds),
            nextRunUtc,
            cancellationToken,
            failureReason);
    }

    public async Task<MigrationBackupReceipt> CreateVerifiedBackupAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        var backup = await CreateBackupAsync(reason, cancellationToken);
        var opened = await OpenBackupAsync(backup.Id, cancellationToken)
            ?? throw new InvalidOperationException("The migration backup was created but could not be reopened for verification.");

        await using var stream = opened.Stream;
        var preview = await PreviewRestoreAsync(stream, cancellationToken);
        if (!preview.Valid || preview.Manifest is null)
        {
            throw new InvalidOperationException(
                $"The migration backup failed verification: {preview.Message}");
        }

        if (!string.Equals(preview.Manifest.App, "Deluno", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The migration backup manifest belongs to a different application.");
        }

        return new MigrationBackupReceipt(
            backup.Id,
            backup.FileName,
            backup.SizeBytes,
            backup.CreatedUtc,
            backup.Reason,
            "manifest-and-restore-preview-verified");
    }

    private sealed class BackupSettingsState
    {
        public bool Enabled { get; set; }
        public string Frequency { get; set; } = "daily";
        public string TimeOfDay { get; set; } = "03:00";
        public int RetentionCount { get; set; } = 7;
        public string BackupFolder { get; set; } = string.Empty;
        public DateTimeOffset? LastRunUtc { get; set; }
        public DateTimeOffset? NextRunUtc { get; set; }

        /// <summary>How this was read. Busy and Unreadable mean these are the standard settings standing in for a file that could not be read.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public StateFileKind Kind { get; set; } = StateFileKind.Ok;

        [System.Text.Json.Serialization.JsonIgnore]
        public string? Problem { get; set; }
    }
}
