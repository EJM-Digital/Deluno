using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
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
    DateTimeOffset ExpiresUtc,
    // #1161: set when the files left the library because a title was removed.
    // The catalogue record is kept beside the files so Restore can put the
    // title back with them, not only the folder.
    string? TitleSnapshotId = null,
    // #1188: set when one file was taken off a title that stays in Deluno (a
    // film's file, an episode's file). Restore puts the file back and then
    // tells the catalogue so the title has its file again.
    RecycleBinFileOwner? FileOwner = null);

/// <summary>
/// The title (and, for a series, the episode) a single recycled file belonged
/// to, and whether it was being watched when the file went (#1188).
/// </summary>
public sealed record RecycleBinFileOwner(
    string MediaType,
    string EntityType,
    string TitleId,
    string Title,
    int? Year,
    string? EpisodeId,
    bool WasMonitored);

/// <summary>
/// The catalogue record of a title that was removed with its files, kept for
/// as long as its files are in the bin (#1161). <c>DataJson</c> is opaque
/// here: only the catalogue that wrote it can read it back.
/// </summary>
public sealed record RecycleBinTitleSnapshot(
    string Id,
    string MediaType,
    string EntityType,
    string TitleId,
    string Title,
    int? Year,
    string DataJson,
    DateTimeOffset CreatedUtc);

/// <summary>What putting a removed title back in Deluno did.</summary>
public sealed record TitleRestoreOutcome(bool Restored, string Message, string? TitleId = null);

/// <summary>
/// Puts a removed title's catalogue record back. Implemented by the catalogue
/// side (Platform cannot reach the movie and TV databases).
/// </summary>
public interface ITitleRestorer
{
    bool Handles(string mediaType);

    Task<TitleRestoreOutcome> RestoreAsync(RecycleBinTitleSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// A single file that was taken off a title which stayed in Deluno is back
    /// on disk (#1188): link it to its film or episode again.
    /// </summary>
    Task<TitleRestoreOutcome> RelinkFileAsync(RecycleBinItem restored, RecycleBinFileOwner owner, CancellationToken cancellationToken)
        => Task.FromResult(new TitleRestoreOutcome(false, string.Empty));

    /// <summary>
    /// The same, for a file whose bin item recorded no owner (binned before
    /// every writer recorded one, #1189): the owner is found from the file's
    /// original library path, and the outcome says plainly when none is found.
    /// </summary>
    Task<TitleRestoreOutcome> RelinkUnownedFileAsync(RecycleBinItem restored, CancellationToken cancellationToken)
        => Task.FromResult(new TitleRestoreOutcome(false, string.Empty));

    /// <summary>
    /// Which film or episode a library file belongs to, found from its path
    /// (#1189). The recycle bin asks this for every file it takes, so no writer
    /// has to remember to name the owner. Null when none can be found.
    /// </summary>
    Task<RecycleBinFileOwner?> FindFileOwnerAsync(string libraryId, string mediaType, string path, CancellationToken cancellationToken)
        => Task.FromResult<RecycleBinFileOwner?>(null);
}

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
    RecycleBinItem? Item = null,
    RecycleBinTitleSnapshot? Title = null,
    // #1161: whether a restore also put the title back in Deluno, and which
    // title page to open. False with a Title means the files are back but the
    // record could not be, and the message says so.
    bool TitleRestored = false,
    string? TitleId = null,
    string? TitleEntityType = null);

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
    /// <summary>
    /// Moves exactly one file of a title that stays in Deluno to the bin (#1188):
    /// never the title folder around it, and never a file outside the library
    /// root. Restore puts the file back where it was and relinks the title.
    /// Null when the file is not inside the library or could not be moved.
    /// </summary>
    Task<RecycleBinItem?> StoreRemovedFileAsync(
        Libraries.Contracts.LibraryItem library,
        string path,
        RecycleBinFileOwner? owner,
        CancellationToken cancellationToken);

    /// <summary>
    /// Keeps a removed title's catalogue record with the items its files
    /// became, so a restore can bring the title back too (#1161).
    /// </summary>
    Task AttachTitleAsync(
        IReadOnlyList<string> itemIds,
        RecycleBinTitleSnapshot title,
        CancellationToken cancellationToken);

    /// <summary>Forgets a title record once the title is back.</summary>
    Task ReleaseTitleAsync(string snapshotId, CancellationToken cancellationToken);

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
    ILogger<RecycleBinService> logger,
    IRealtimeEventPublisher? realtime = null,
    IServiceScopeFactory? scopeFactory = null,
    IStateFileSystem? stateFiles = null)
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

                // #1189: every file taken names its owner, found while the title still has it.
                var owner = unit.IsDirectory ? null : await FindOwnerAsync(unit.Library, unit.OriginalPath, cancellationToken);
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
                        ExpiresUtc: now.AddDays(state.Settings.RetentionDays),
                        FileOwner: unit.IsDirectory ? null : owner);
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

    public Task<RecycleBinItem?> StoreReplacementAsync(
        Libraries.Contracts.LibraryItem library,
        string originalPath,
        string existingPath,
        CancellationToken cancellationToken)
        => StoreFileAsync(library, originalPath, existingPath, owner: null, cancellationToken);

    public async Task<RecycleBinItem?> StoreRemovedFileAsync(
        Libraries.Contracts.LibraryItem library,
        string path,
        RecycleBinFileOwner? owner,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(library.RootPath))
        {
            return null;
        }

        string full;
        try
        {
            var root = Path.GetFullPath(library.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            full = Path.GetFullPath(path);
            if (!IsInsideRoot(full, root))
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return await StoreFileAsync(library, full, full, owner, cancellationToken);
    }

    private async Task<RecycleBinFileOwner?> FindOwnerAsync(
        Libraries.Contracts.LibraryItem library,
        string path,
        CancellationToken cancellationToken)
    {
        if (scopeFactory is null)
        {
            return null;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            foreach (var restorer in scope.ServiceProvider.GetServices<ITitleRestorer>())
            {
                if (!restorer.Handles(library.MediaType))
                {
                    continue;
                }

                var owner = await restorer.FindFileOwnerAsync(library.Id, library.MediaType, path, cancellationToken);
                if (owner is not null)
                {
                    return owner;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Naming the owner is a help for Restore, never a reason to keep a file from the bin.
            logger.LogDebug(exception, "Could not find which title {Path} belongs to.", path);
        }

        return null;
    }

    private async Task<RecycleBinItem?> StoreFileAsync(
        Libraries.Contracts.LibraryItem library,
        string originalPath,
        string existingPath,
        RecycleBinFileOwner? owner,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(existingPath) || string.IsNullOrWhiteSpace(library.RootPath))
            {
                return null;
            }

            // #1189: whoever moves a file, the bin records which film or episode it belonged to.
            owner ??= await FindOwnerAsync(library, originalPath, cancellationToken);
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
                    ExpiresUtc: now.AddDays(state.Settings.RetentionDays),
                    FileOwner: owner);
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

    public async Task AttachTitleAsync(
        IReadOnlyList<string> itemIds,
        RecycleBinTitleSnapshot title,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            var attached = false;
            for (var index = 0; index < state.Items.Count; index++)
            {
                if (itemIds.Contains(state.Items[index].Id, StringComparer.OrdinalIgnoreCase))
                {
                    state.Items[index] = state.Items[index] with { TitleSnapshotId = title.Id };
                    attached = true;
                }
            }

            if (attached)
            {
                state.Titles.RemoveAll(existing => string.Equals(existing.Id, title.Id, StringComparison.OrdinalIgnoreCase));
                state.Titles.Add(title);
                await WriteStateAsync(state, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReleaseTitleAsync(string snapshotId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            state.Titles.RemoveAll(existing => string.Equals(existing.Id, snapshotId, StringComparison.OrdinalIgnoreCase));
            for (var index = 0; index < state.Items.Count; index++)
            {
                if (string.Equals(state.Items[index].TitleSnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase))
                {
                    state.Items[index] = state.Items[index] with { TitleSnapshotId = null };
                }
            }

            await WriteStateAsync(state, cancellationToken);
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
                var title = item.TitleSnapshotId is null
                    ? null
                    : state.Titles.FirstOrDefault(entry => string.Equals(entry.Id, item.TitleSnapshotId, StringComparison.OrdinalIgnoreCase));
                return new RecycleBinOperationResult(true, $"Restored {Path.GetFileName(item.OriginalPath)} to its original library path.", item, title);
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
        var read = StateFile.Read<RecycleBinState>(StatePath, JsonOptions, stateFiles);
        switch (read.Kind)
        {
            case StateFileKind.Missing:
                var initial = new RecycleBinState { Settings = DefaultSettings() };
                await WriteStateAsync(initial, cancellationToken);
                return initial;
            case StateFileKind.Busy:
                // Not "empty" (#1260): the next write would forget every item in the bin.
                throw new StateFileException("The recycle bin's list is in use by something else right now, so nothing was changed. Try again in a moment.");
            case StateFileKind.Unreadable:
                var kept = StateFile.KeepUnreadableCopy(StatePath, stateFiles);
                throw new StateFileException(StateFile.UnreadableWords("The recycle bin's list", kept));
        }

        var state = read.Value!;
        state.Settings = NormalizeSettings(state.Settings);
        state.Items ??= [];
        state.Titles ??= [];
        return state;
    }

    /// <summary>
    /// Writes the list atomically and waits out a busy file (a backup, a scanner, a reader holding the old one) for a couple of
    /// seconds. The files have already been moved by the time this runs, so a list that is not recorded would lose items:
    /// a failure throws, in everyday words, instead of being swallowed (#1260).
    /// </summary>
    private async Task WriteStateAsync(RecycleBinState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RecycleStateRoot);
        if (StateFile.WriteJson(StatePath, state, JsonOptions, stateFiles) is { } problem)
        {
            throw new StateFileException($"The recycle bin's list could not be saved: {problem}.");
        }

        await realtime.PublishRecycleBinChangedAsync(cancellationToken);
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

        // A removed title's record only matters while some of its files are still here.
        state.Titles.RemoveAll(title => !state.Items.Any(item =>
            string.Equals(item.TitleSnapshotId, title.Id, StringComparison.OrdinalIgnoreCase)) &&
            title.CreatedUtc < now.AddDays(-state.Settings.RetentionDays));
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
        public List<RecycleBinTitleSnapshot> Titles { get; set; } = [];
    }

    private sealed record MoveUnit(
        Libraries.Contracts.LibraryItem Library,
        string OriginalPath,
        bool IsDirectory);

    private sealed record UnitSet(
        IReadOnlyList<MoveUnit> Items,
        IReadOnlyList<string> Warnings);
}
