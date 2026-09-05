using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Platform;

public sealed record RecycleBinSettings(
    int RetentionDays,
    long MaxSizeMb);

public sealed record RecycleBinItem(
    string Id,
    string LibraryId,
    string LibraryName,
    string MediaType,
    string OriginalPath,
    string RecyclePath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc);

/// <param name="Items">Exactly what a manual empty would take, in the order it would take it.</param>
/// <param name="ExpiredCount">Items past their retention date.</param>
/// <param name="OverCapacityCount">
/// Items that have not expired but are going anyway because the bin is over
/// its size limit. Counted separately because those are the ones somebody
/// might want back, and a total on its own would hide them.
/// </param>
public sealed record RecycleBinCleanupPreview(
    IReadOnlyList<RecycleBinItem> Items,
    int ExpiredCount,
    int OverCapacityCount,
    long BytesFreed);

public sealed record RecycleBinMoveResult(
    int MovedFileCount,
    int MovedFolderCount,
    IReadOnlyList<RecycleBinItem> Items,
    IReadOnlyList<string> Warnings);

public sealed record RecycleBinOperationResult(
    bool Success,
    string Message,
    RecycleBinItem? Item = null);

public interface IRecycleBinService
{
    Task<RecycleBinSettings> GetSettingsAsync(CancellationToken cancellationToken);
    Task<RecycleBinSettings> SaveSettingsAsync(RecycleBinSettings settings, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecycleBinItem>> ListAsync(CancellationToken cancellationToken);
    Task<RecycleBinMoveResult> MoveAsync(
        IReadOnlyList<TrackedLibraryFile> trackedFiles,
        IReadOnlyList<Libraries.Contracts.LibraryItem> libraries,
        CancellationToken cancellationToken);
    Task<RecycleBinItem?> StoreReplacementAsync(
        Libraries.Contracts.LibraryItem library,
        string originalPath,
        string existingPath,
        CancellationToken cancellationToken);
    Task<RecycleBinOperationResult> RestoreAsync(string id, CancellationToken cancellationToken);
    Task<RecycleBinOperationResult> PermanentlyDeleteAsync(string id, CancellationToken cancellationToken);
    Task<int> CleanupAsync(CancellationToken cancellationToken);

    /// <summary>
    /// What a manual empty would take, without taking it.
    ///
    /// <para>DESIGN-007 decision 15: <i>"Enforce retention automatically, and
    /// show what a manual empty takes"</i>. Permanent deletion that only tells
    /// you afterwards is not a choice, it is a report.</para>
    /// </summary>
    Task<RecycleBinCleanupPreview> PreviewCleanupAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A recoverable holding area for files removed from a library. The holding
/// directory sits beside each library root, so a move stays on the same volume;
/// the index lives under Deluno's data root and records the original path.
/// </summary>
public sealed class RecycleBinService(
    IOptions<StoragePathOptions> storageOptions,
    TimeProvider timeProvider,
    ILogger<RecycleBinService> logger)
    : IRecycleBinService
{
    private const int DefaultRetentionDays = 30;
    private const long DefaultMaxSizeMb = 102_400;
    private const int MaxRetentionDays = 365;
    private const long MaxSizeMb = 10_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    private string DataRoot => Path.GetFullPath(storageOptions.Value.DataRoot);
    private string RecycleStateRoot => Path.Combine(DataRoot, "recycle-bin");
    private string StatePath => Path.Combine(RecycleStateRoot, "state.json");

    public async Task<RecycleBinSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            return state.Settings;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RecycleBinSettings> SaveSettingsAsync(
        RecycleBinSettings settings,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            state.Settings = NormalizeSettings(settings);
            await EnforceRetentionUnsafeAsync(state, cancellationToken);
            await WriteStateAsync(state, cancellationToken);
            return state.Settings;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<RecycleBinItem>> ListAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            return state.Items
                .OrderByDescending(item => item.CreatedUtc)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RecycleBinMoveResult> MoveAsync(
        IReadOnlyList<TrackedLibraryFile> trackedFiles,
        IReadOnlyList<Libraries.Contracts.LibraryItem> libraries,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            var librariesById = libraries.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var units = BuildUnits(trackedFiles, librariesById);
            var moved = new List<RecycleBinItem>();
            var warnings = units.Warnings.ToList();
            var now = timeProvider.GetUtcNow();

            foreach (var unit in units.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Exists(unit.OriginalPath, unit.IsDirectory))
                {
                    continue;
                }

                var id = Guid.CreateVersion7().ToString("N");
                var holdingRoot = HoldingRoot(unit.Library.RootPath!);
                var itemRoot = Path.Combine(holdingRoot, id);
                var destination = Path.Combine(itemRoot, Path.GetFileName(unit.OriginalPath));

                try
                {
                    Directory.CreateDirectory(itemRoot);
                    var sizeBytes = CalculateSize(unit.OriginalPath, unit.IsDirectory);
                    if (unit.IsDirectory)
                    {
                        Directory.Move(unit.OriginalPath, destination);
                    }
                    else
                    {
                        File.Move(unit.OriginalPath, destination);
                    }

                    var item = new RecycleBinItem(
                        Id: id,
                        LibraryId: unit.Library.Id,
                        LibraryName: unit.Library.Name,
                        MediaType: unit.Library.MediaType,
                        OriginalPath: unit.OriginalPath,
                        RecyclePath: destination,
                        IsDirectory: unit.IsDirectory,
                        SizeBytes: sizeBytes,
                        CreatedUtc: now,
                        ExpiresUtc: now.AddDays(state.Settings.RetentionDays));
                    state.Items.Add(item);
                    moved.Add(item);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    TryDeleteEmptyDirectory(itemRoot);
                    warnings.Add($"{unit.Library.Name}: Deluno could not move {unit.OriginalPath} to the recycle bin. {exception.Message}");
                }
            }

            if (moved.Count > 0)
            {
                await EnforceRetentionUnsafeAsync(state, cancellationToken);
                await WriteStateAsync(state, cancellationToken);
            }

            return new RecycleBinMoveResult(
                MovedFileCount: moved.Count(item => !item.IsDirectory),
                MovedFolderCount: moved.Count(item => item.IsDirectory),
                Items: moved,
                Warnings: warnings.Distinct(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RecycleBinItem?> StoreReplacementAsync(
        Libraries.Contracts.LibraryItem library,
        string originalPath,
        string existingPath,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(existingPath) || string.IsNullOrWhiteSpace(library.RootPath))
            {
                return null;
            }

            var state = await ReadStateAsync(cancellationToken);
            var id = Guid.CreateVersion7().ToString("N");
            var holdingRoot = HoldingRoot(library.RootPath);
            var itemRoot = Path.Combine(holdingRoot, id);
            var destination = Path.Combine(itemRoot, Path.GetFileName(originalPath));

            try
            {
                Directory.CreateDirectory(itemRoot);
                var sizeBytes = CalculateSize(existingPath, isDirectory: false);
                File.Move(existingPath, destination);
                var now = timeProvider.GetUtcNow();
                var item = new RecycleBinItem(
                    Id: id,
                    LibraryId: library.Id,
                    LibraryName: library.Name,
                    MediaType: library.MediaType,
                    OriginalPath: originalPath,
                    RecyclePath: destination,
                    IsDirectory: false,
                    SizeBytes: sizeBytes,
                    CreatedUtc: now,
                    ExpiresUtc: now.AddDays(state.Settings.RetentionDays));
                state.Items.Add(item);
                await EnforceRetentionUnsafeAsync(state, cancellationToken);
                await WriteStateAsync(state, cancellationToken);
                return item;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDeleteEmptyDirectory(itemRoot);
                logger.LogWarning(exception, "Could not retain replaced file {ExistingPath} in the recycle bin.", existingPath);
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RecycleBinOperationResult> RestoreAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            var item = FindItem(state, id);
            if (item is null)
            {
                return new RecycleBinOperationResult(false, "Recycle-bin item not found.");
            }

            if (!Exists(item.RecyclePath, item.IsDirectory))
            {
                state.Items.Remove(item);
                await WriteStateAsync(state, cancellationToken);
                return new RecycleBinOperationResult(false, "The stored recycle-bin item is no longer on disk.");
            }

            if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
            {
                return new RecycleBinOperationResult(false, "The original library path is occupied. Move or remove it before restoring this item.", item);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                if (item.IsDirectory)
                {
                    Directory.Move(item.RecyclePath, item.OriginalPath);
                }
                else
                {
                    File.Move(item.RecyclePath, item.OriginalPath);
                }

                state.Items.Remove(item);
                TryDeleteEmptyDirectory(Path.GetDirectoryName(item.RecyclePath));
                await WriteStateAsync(state, cancellationToken);
                return new RecycleBinOperationResult(true, $"Restored {Path.GetFileName(item.OriginalPath)} to its original library path.", item);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new RecycleBinOperationResult(false, $"Restore failed: {exception.Message}", item);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RecycleBinOperationResult> PermanentlyDeleteAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            var item = FindItem(state, id);
            if (item is null)
            {
                return new RecycleBinOperationResult(false, "Recycle-bin item not found.");
            }

            try
            {
                DeletePath(item.RecyclePath, item.IsDirectory);
                state.Items.Remove(item);
                TryDeleteEmptyDirectory(Path.GetDirectoryName(item.RecyclePath));
                await WriteStateAsync(state, cancellationToken);
                return new RecycleBinOperationResult(true, $"Permanently deleted {Path.GetFileName(item.OriginalPath)}.", item);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new RecycleBinOperationResult(false, $"Permanent deletion failed: {exception.Message}", item);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            var removed = await EnforceRetentionUnsafeAsync(state, cancellationToken);
            if (removed > 0)
            {
                await WriteStateAsync(state, cancellationToken);
            }

            return removed;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RecycleBinState> ReadStateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RecycleStateRoot);
        if (!File.Exists(StatePath))
        {
            var initial = new RecycleBinState { Settings = DefaultSettings() };
            await WriteStateAsync(initial, cancellationToken);
            return initial;
        }

        await using var stream = File.OpenRead(StatePath);
        var state = await JsonSerializer.DeserializeAsync<RecycleBinState>(stream, JsonOptions, cancellationToken)
            ?? new RecycleBinState();
        state.Settings = NormalizeSettings(state.Settings);
        state.Items ??= [];
        return state;
    }

    private async Task WriteStateAsync(RecycleBinState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RecycleStateRoot);
        var temporary = $"{StatePath}.{Guid.CreateVersion7():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            }

            File.Move(temporary, StatePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                TryDeleteFile(temporary);
            }
        }
    }

    private async Task<int> EnforceRetentionUnsafeAsync(
        RecycleBinState state,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var removed = 0;

        foreach (var item in ItemsRetentionWouldTake(state, now))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DeletePath(item.RecyclePath, item.IsDirectory);
                state.Items.Remove(item);
                TryDeleteEmptyDirectory(Path.GetDirectoryName(item.RecyclePath));
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not expire recycle-bin item {RecycleBinItemId}.", item.Id);
            }
        }

        return removed;
    }

    /// <summary>
    /// Which items retention says should go, in the order it would take them:
    /// expired first, oldest first, then whatever else it takes to get back
    /// under the size limit.
    ///
    /// <para>Chosen once and used by both the deleting and the showing, so the
    /// preview cannot promise one thing and the empty do another. Deciding this
    /// twice is exactly how a "this will remove 3 items" dialog ends up
    /// removing five.</para>
    ///
    /// <para>It assumes each deletion succeeds. It previously recounted the
    /// bin's size from the surviving items on every step, which meant a file
    /// Deluno could not delete made it delete <em>another</em> one to make up
    /// the space. Failing to free space is a reason to stop and retry, not a
    /// reason to take more than was shown.</para>
    /// </summary>
    private static IReadOnlyList<RecycleBinItem> ItemsRetentionWouldTake(RecycleBinState state, DateTimeOffset now)
    {
        var candidates = state.Items
            .Where(item => item.ExpiresUtc <= now)
            .OrderBy(item => item.ExpiresUtc)
            .Concat(state.Items.OrderBy(item => item.CreatedUtc))
            .DistinctBy(item => item.Id)
            .ToList();

        var capacityBytes = state.Settings.MaxSizeMb * 1024L * 1024L;
        var remainingBytes = state.Items.Sum(entry => Math.Max(0, entry.SizeBytes));
        var taking = new List<RecycleBinItem>();

        foreach (var item in candidates)
        {
            if (item.ExpiresUtc > now && remainingBytes <= capacityBytes)
            {
                break;
            }

            taking.Add(item);
            remainingBytes -= Math.Max(0, item.SizeBytes);
        }

        return taking;
    }

    public async Task<RecycleBinCleanupPreview> PreviewCleanupAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            var taking = ItemsRetentionWouldTake(state, now);

            return new RecycleBinCleanupPreview(
                taking,
                taking.Count(item => item.ExpiresUtc <= now),
                taking.Count(item => item.ExpiresUtc > now),
                taking.Sum(item => Math.Max(0, item.SizeBytes)));
        }
        finally
        {
            gate.Release();
        }
    }

    private static UnitSet BuildUnits(
        IReadOnlyList<TrackedLibraryFile> trackedFiles,
        IReadOnlyDictionary<string, Libraries.Contracts.LibraryItem> librariesById)
    {
        var units = new Dictionary<string, MoveUnit>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        foreach (var trackedFile in trackedFiles)
        {
            if (!librariesById.TryGetValue(trackedFile.LibraryId, out var library) || string.IsNullOrWhiteSpace(library.RootPath))
            {
                warnings.Add("A tracked path was skipped because its library root is no longer configured.");
                continue;
            }

            try
            {
                var root = Path.GetFullPath(library.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var path = Path.GetFullPath(trackedFile.FilePath);
                if (!IsInsideRoot(path, root))
                {
                    warnings.Add($"{library.Name}: a tracked path was skipped because it is outside the library root.");
                    continue;
                }

                if (File.Exists(path))
                {
                    var titleFolder = TryGetTitleFolder(path, root);
                    var unitPath = titleFolder is not null && Directory.Exists(titleFolder) ? titleFolder : path;
                    var isDirectory = Directory.Exists(unitPath);
                    units[$"{library.Id}:{unitPath}"] = new MoveUnit(library, unitPath, isDirectory);
                }
            }
            catch
            {
                warnings.Add($"{library.Name}: the configured library root could not be read.");
            }
        }

        return new UnitSet(units.Values.ToArray(), warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static RecycleBinItem? FindItem(RecycleBinState state, string id)
        => state.Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string HoldingRoot(string libraryRoot)
        => Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".deluno-recycle-bin";

    private static string? TryGetTitleFolder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator > 0 ? Path.Combine(root, relative[..separator]) : null;
    }

    private static bool IsInsideRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison) ||
               path.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool Exists(string path, bool isDirectory)
        => isDirectory ? Directory.Exists(path) : File.Exists(path);

    private static long CalculateSize(string path, bool isDirectory)
    {
        if (!isDirectory)
        {
            return new FileInfo(path).Length;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total = checked(total + new FileInfo(file).Length);
            }
            catch (IOException)
            {
                // Size is informational; a locked file can still be moved.
            }
        }

        return total;
    }

    private static void DeletePath(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static RecycleBinSettings DefaultSettings()
        => new(DefaultRetentionDays, DefaultMaxSizeMb);

    private static RecycleBinSettings NormalizeSettings(RecycleBinSettings? settings)
        => new(
            Math.Clamp(settings?.RetentionDays ?? DefaultRetentionDays, 1, MaxRetentionDays),
            Math.Clamp(settings?.MaxSizeMb ?? DefaultMaxSizeMb, 1, MaxSizeMb));

    private sealed class RecycleBinState
    {
        public RecycleBinSettings Settings { get; set; } = new(DefaultRetentionDays, DefaultMaxSizeMb);
        public List<RecycleBinItem> Items { get; set; } = [];
    }

    private sealed record MoveUnit(
        Libraries.Contracts.LibraryItem Library,
        string OriginalPath,
        bool IsDirectory);

    private sealed record UnitSet(
        IReadOnlyList<MoveUnit> Items,
        IReadOnlyList<string> Warnings);
}
