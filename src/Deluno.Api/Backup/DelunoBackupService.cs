using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
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
    Task<RestoreResultResponse> RestoreAsync(Stream backupStream, CancellationToken cancellationToken);
}

public sealed class DelunoBackupService(
    IOptions<StoragePathOptions> storageOptions,
    TimeProvider timeProvider,
    ILogger<DelunoBackupService> logger)
    : BackgroundService, IDelunoBackupService
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
        var state = await ReadSettingsStateAsync(cancellationToken);
        state.Enabled = request.Enabled;
        state.Frequency = NormalizeFrequency(request.Frequency);
        state.TimeOfDay = NormalizeTimeOfDay(request.TimeOfDay);
        state.RetentionCount = Math.Clamp(request.RetentionCount ?? state.RetentionCount, 1, 100);
        state.BackupFolder = NormalizeBackupFolder(request.BackupFolder);
        state.NextRunUtc = CalculateNextRun(state, timeProvider.GetUtcNow());
        await WriteSettingsStateAsync(state, cancellationToken);
        return ToSnapshot(state);
    }

    public async Task<IReadOnlyList<BackupItem>> ListBackupsAsync(CancellationToken cancellationToken)
    {
        var state = await ReadSettingsStateAsync(cancellationToken);
        Directory.CreateDirectory(state.BackupFolder);

        return Directory
            .EnumerateFiles(state.BackupFolder, "*.zip", SearchOption.TopDirectoryOnly)
            .Select(ReadBackupItem)
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.CreatedUtc)
            .ToArray();
    }

    public async Task<BackupItem> CreateBackupAsync(string reason, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadSettingsStateAsync(cancellationToken);
            Directory.CreateDirectory(state.BackupFolder);

            var now = timeProvider.GetUtcNow();
            var safeReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason.Trim();
            var id = $"deluno-backup-{now:yyyyMMdd-HHmmss}";
            var targetPath = Path.Combine(state.BackupFolder, $"{id}.zip");
            var dataFiles = EnumerateDataFiles().ToArray();
            var manifest = new BackupManifest(
                App: "Deluno",
                Version: GetVersion(),
                CreatedUtc: now,
                Reason: safeReason,
                Files: dataFiles.Select(file => Path.GetRelativePath(DataRoot, file)).Order(StringComparer.OrdinalIgnoreCase).ToArray());

            using (var fileStream = File.Create(targetPath))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry("deluno-backup.json", CompressionLevel.Optimal);
                await using (var manifestStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken);
                }

                foreach (var file in dataFiles)
                {
                    var relativePath = Path.GetRelativePath(DataRoot, file).Replace('\\', '/');
                    var entry = archive.CreateEntry($"data/{relativePath}", CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(file);
                    using var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var entryStream = entry.Open();
                    sourceStream.CopyTo(entryStream);
                }
            }

            state.LastRunUtc = now;
            state.NextRunUtc = CalculateNextRun(state, now);
            await WriteSettingsStateAsync(state, cancellationToken);
            await EnforceRetentionAsync(state, cancellationToken);

            logger.LogInformation("Created Deluno backup {BackupPath}.", targetPath);
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
        var file = ResolveBackupPath(state.BackupFolder, id);
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
        var file = ResolveBackupPath(state.BackupFolder, id);
        if (file is null || !File.Exists(file))
        {
            return false;
        }

        File.Delete(file);
        await Task.CompletedTask;
        return true;
    }

    public async Task<RestorePreviewResponse> PreviewRestoreAsync(Stream backupStream, CancellationToken cancellationToken)
    {
        await using var buffered = new MemoryStream();
        await backupStream.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;
        return await PreviewRestoreCoreAsync(buffered, cancellationToken);
    }

    public async Task<RestoreResultResponse> RestoreAsync(Stream backupStream, CancellationToken cancellationToken)
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

            buffered.Position = 0;
            var restoreRoot = Path.Combine(DataRoot, "restore-staging", timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(restoreRoot);
            var restored = new List<string>();

            using var archive = new ZipArchive(buffered, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("data/", StringComparison.OrdinalIgnoreCase)))
            {
                var relative = entry.FullName["data/".Length..].Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(relative) || relative.Contains("..", StringComparison.Ordinal))
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(DataRoot, relative));
                if (!target.StartsWith(DataRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var backupExisting = target + ".pre-restore";
                if (File.Exists(target))
                {
                    File.Copy(target, backupExisting, overwrite: true);
                }

                entry.ExtractToFile(target, overwrite: true);
                restored.Add(Path.GetRelativePath(DataRoot, target));
            }

            logger.LogWarning("Restored Deluno data files from uploaded backup. Restart is recommended.");
            return new RestoreResultResponse(
                Restored: restored.Count > 0,
                Message: restored.Count > 0
                    ? "Backup restored. Restart Deluno before continuing so every database connection reloads cleanly."
                    : "No restorable data files were found.",
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
                    await CreateBackupAsync("scheduled", stoppingToken);
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

            return new RestorePreviewResponse(true, "Backup can be restored.", manifest, warnings);
        }
        catch (Exception ex)
        {
            return new RestorePreviewResponse(false, ex.Message, null, []);
        }
    }

    private IEnumerable<string> EnumerateDataFiles()
    {
        Directory.CreateDirectory(DataRoot);
        var excludedRoots = new[]
        {
            Path.Combine(DataRoot, "backups"),
            Path.Combine(DataRoot, "restore-staging")
        };

        return Directory
            .EnumerateFiles(DataRoot, "*", SearchOption.AllDirectories)
            .Where(file => excludedRoots.All(excluded => !Path.GetFullPath(file).StartsWith(Path.GetFullPath(excluded), StringComparison.OrdinalIgnoreCase)))
            .Where(file => !file.EndsWith(".pre-restore", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase));
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
        catch
        {
            return null;
        }
    }

    private async Task EnforceRetentionAsync(BackupSettingsState state, CancellationToken cancellationToken)
    {
        var items = await ListBackupsAsync(cancellationToken);
        foreach (var item in items.Skip(state.RetentionCount))
        {
            File.Delete(item.FullPath);
        }
    }

    private async Task<BackupSettingsState> ReadSettingsStateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DefaultBackupFolder);
        if (!File.Exists(SettingsPath))
        {
            var initial = new BackupSettingsState
            {
                Enabled = false,
                Frequency = "daily",
                TimeOfDay = "03:00",
                RetentionCount = 7,
                BackupFolder = DefaultBackupFolder,
                LastRunUtc = null,
                NextRunUtc = null
            };
            await WriteSettingsStateAsync(initial, cancellationToken);
            return initial;
        }

        await using var stream = File.OpenRead(SettingsPath);
        var state = await JsonSerializer.DeserializeAsync<BackupSettingsState>(stream, JsonOptions, cancellationToken) ?? new BackupSettingsState();
        state.Frequency = NormalizeFrequency(state.Frequency);
        state.TimeOfDay = NormalizeTimeOfDay(state.TimeOfDay);
        state.RetentionCount = Math.Clamp(state.RetentionCount, 1, 100);
        state.BackupFolder = NormalizeBackupFolder(state.BackupFolder);
        if (state.Enabled && state.NextRunUtc is null)
        {
            state.NextRunUtc = CalculateNextRun(state, timeProvider.GetUtcNow());
            await WriteSettingsStateAsync(state, cancellationToken);
        }
        return state;
    }

    private async Task WriteSettingsStateAsync(BackupSettingsState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DefaultBackupFolder);
        Directory.CreateDirectory(state.BackupFolder);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
    }

    private BackupSettingsSnapshot ToSnapshot(BackupSettingsState state)
        => new(
            state.Enabled,
            state.Frequency,
            state.TimeOfDay,
            state.RetentionCount,
            state.BackupFolder,
            state.LastRunUtc,
            state.NextRunUtc);

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

    private string NormalizeBackupFolder(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? DefaultBackupFolder : value.Trim();
        return Path.GetFullPath(path);
    }

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
        return path.StartsWith(Path.GetFullPath(backupFolder), StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static string GetVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

    private sealed class BackupSettingsState
    {
        public bool Enabled { get; set; }
        public string Frequency { get; set; } = "daily";
        public string TimeOfDay { get; set; } = "03:00";
        public int RetentionCount { get; set; } = 7;
        public string BackupFolder { get; set; } = string.Empty;
        public DateTimeOffset? LastRunUtc { get; set; }
        public DateTimeOffset? NextRunUtc { get; set; }
    }
}
