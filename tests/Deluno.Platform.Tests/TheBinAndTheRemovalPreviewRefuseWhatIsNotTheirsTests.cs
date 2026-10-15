using Deluno.Libraries.Contracts;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Platform.Tests;

/// <summary>
/// The refusals of the recycle bin and the removal preview, on real temp folders: a tracked path whose library is
/// gone, whose root is blank or unreadable, or that lies outside the root is named in a warning and never touched,
/// and the download-only entry points never take anything inside a library root.
/// </summary>
public sealed class TheBinAndTheRemovalPreviewRefuseWhatIsNotTheirsTests : IDisposable
{
    private readonly string sandbox = Path.Combine(Path.GetTempPath(), $"deluno-refuse-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([sandbox, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private RecycleBinService NewBin()
        => new(Options.Create(new StoragePathOptions { DataRoot = Dir("data") }), TimeProvider.System, NullLogger<RecycleBinService>.Instance);

    [Fact]
    public async Task The_bin_skips_a_path_whose_library_is_gone_or_has_no_root_and_moves_nothing()
    {
        var root = Dir("library");
        var file = Path.Combine(root, "Film.mkv");
        File.WriteAllText(file, "film");

        var result = await NewBin().MoveAsync(
            [new TrackedLibraryFile("missing", file), new TrackedLibraryFile("blank", file)],
            [Library("blank", "  ")],
            CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Contains(result.Warnings, w => w.Contains("no longer configured", StringComparison.Ordinal));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task The_bin_refuses_a_path_outside_the_library_root_and_names_it()
    {
        var root = Dir("library");
        var outside = Path.Combine(Dir("elsewhere"), "Keep.mkv");
        File.WriteAllText(outside, "keep");

        var result = await NewBin().MoveAsync([new TrackedLibraryFile("l", outside)], [Library("l", root)], CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Contains(result.Warnings, w => w.Contains("outside the library root", StringComparison.Ordinal));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task The_bin_says_a_root_it_cannot_read_could_not_be_read_and_takes_nothing()
    {
        var file = Path.Combine(Dir("library"), "Film.mkv");
        File.WriteAllText(file, "film");

        var result = await NewBin().MoveAsync([new TrackedLibraryFile("l", file)], [Library("l", "bad\0root")], CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Contains(result.Warnings, w => w.Contains("could not be read", StringComparison.Ordinal));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task The_bin_takes_a_download_path_only_from_beside_the_library_never_from_inside_it()
    {
        var bin = NewBin();
        var root = Dir("library");
        var library = Library("l", root);
        var inside = Path.Combine(root, "Film.mkv");
        File.WriteAllText(inside, "film");
        var downloads = Dir("downloads");
        var leftover = Path.Combine(downloads, "Film.part");
        File.WriteAllText(leftover, "partial");
        var leftoverFolder = Dir("downloads", "Film.Folder");
        File.WriteAllText(Path.Combine(leftoverFolder, "a.txt"), "a");

        Assert.Null(await bin.StoreDownloadFileAsync(library, inside, CancellationToken.None));
        Assert.Null(await bin.StoreDownloadFileAsync(library, root, CancellationToken.None));
        Assert.Null(await bin.StoreDownloadFileAsync(library, " ", CancellationToken.None));
        Assert.Null(await bin.StoreDownloadFileAsync(library, Path.Combine(downloads, "..", "downloads", "Film.part"), CancellationToken.None));
        Assert.Null(await bin.StoreDownloadFileAsync(library, "bad\0path", CancellationToken.None));
        Assert.Null(await bin.StoreDownloadFileAsync(Library("l", " "), leftover, CancellationToken.None));
        Assert.Null(await bin.StoreDownloadFolderAsync(library, root, CancellationToken.None));
        Assert.Null(await bin.StoreDownloadFolderAsync(library, Path.Combine(downloads, "NoSuchFolder"), CancellationToken.None));
        Assert.True(File.Exists(inside));
        Assert.True(File.Exists(leftover));

        var folderItem = await bin.StoreDownloadFolderAsync(library, leftoverFolder, CancellationToken.None);
        Assert.NotNull(folderItem);
        Assert.False(Directory.Exists(leftoverFolder));
        Assert.True(folderItem!.IsDirectory);
        var fileItem = await bin.StoreDownloadFileAsync(library, leftover, CancellationToken.None);
        Assert.NotNull(fileItem);
        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public void The_removal_preview_names_what_it_skips_and_lists_a_sidecar_beside_a_shared_folder()
    {
        var root = Dir("library");
        var shared = Dir("library", "Shared Folder");
        var film = Path.Combine(shared, "Film.2026.mkv");
        var subtitle = Path.Combine(shared, "Film.2026.srt");
        var other = Path.Combine(shared, "Other.Film.mkv");
        File.WriteAllText(film, "film");
        File.WriteAllText(subtitle, "subs");
        File.WriteAllText(other, "another title's film");
        var libraries = new[] { Library("l", root), Library("blank", ""), Library("bad", "bad\0root") };

        var preview = LibraryMediaDeletion.Preview(
            [
                new TrackedLibraryFile("l", film),
                new TrackedLibraryFile("l", "bad\0path"),
                new TrackedLibraryFile("gone", film),
                new TrackedLibraryFile("blank", film),
                new TrackedLibraryFile("bad", film),
            ],
            libraries);

        Assert.Equal(new[] { film, subtitle }.OrderBy(p => p, StringComparer.Ordinal), preview.FilePaths.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Empty(preview.FolderPaths);
        Assert.Contains(preview.Warnings, w => w.Contains("outside the library root", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, w => w.Contains("no longer configured", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, w => w.Contains("could not be read", StringComparison.Ordinal));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task A_restorer_that_knows_nothing_extra_answers_the_optional_questions_with_no()
    {
        ITitleRestorer restorer = new BareRestorer();
        var now = DateTimeOffset.UtcNow;
        var item = new RecycleBinItem("i", "l", "L", "movies", "o", "r", false, 1, now, now);
        var owner = new RecycleBinFileOwner("movies", "movie", "t", "T", 2026, null, true);

        Assert.False((await restorer.RelinkFileAsync(item, owner, CancellationToken.None)).Restored);
        Assert.False((await restorer.RelinkUnownedFileAsync(item, CancellationToken.None)).Restored);
        Assert.Null(await restorer.FindFileOwnerAsync("l", "movies", "p", CancellationToken.None));

        IRecycleBinService bin = new BareBin();
        Assert.Null(await bin.StoreStrayOutputAsync(Library("l", "x"), "p", CancellationToken.None));
    }

    private sealed class BareRestorer : ITitleRestorer
    {
        public bool Handles(string mediaType) => true;
        public Task<TitleRestoreOutcome> RestoreAsync(RecycleBinTitleSnapshot snapshot, CancellationToken cancellationToken)
            => Task.FromResult(new TitleRestoreOutcome(false, string.Empty));
    }

    private sealed class BareBin : IRecycleBinService
    {
        public Task<RecycleBinSettings> GetSettingsAsync(CancellationToken c) => throw new NotSupportedException();
        public Task<RecycleBinSettings> SaveSettingsAsync(RecycleBinSettings s, CancellationToken c) => throw new NotSupportedException();
        public Task<IReadOnlyList<RecycleBinItem>> ListAsync(CancellationToken c) => throw new NotSupportedException();
        public Task<RecycleBinMoveResult> MoveAsync(IReadOnlyList<TrackedLibraryFile> t, IReadOnlyList<LibraryItem> l, CancellationToken c) => throw new NotSupportedException();
        public Task<RecycleBinItem?> StoreReplacementAsync(LibraryItem l, string o, string e, CancellationToken c) => throw new NotSupportedException();
        public Task<RecycleBinItem?> StoreRemovedFileAsync(LibraryItem l, string p, RecycleBinFileOwner? o, CancellationToken c) => throw new NotSupportedException();
        public Task<RecycleBinItem?> StoreDownloadFileAsync(LibraryItem l, string p, CancellationToken c) => throw new NotSupportedException();
        public Task<RecycleBinItem?> StoreDownloadFolderAsync(LibraryItem l, string p, CancellationToken c) => throw new NotSupportedException();
        public Task AttachTitleAsync(IReadOnlyList<string> i, RecycleBinTitleSnapshot t, CancellationToken c) => throw new NotSupportedException();
        public Task ReleaseTitleAsync(string s, CancellationToken c) => throw new NotSupportedException();
        public Task<RecycleBinOperationResult> RestoreAsync(string id, CancellationToken c) => throw new NotSupportedException();
        public Task<RecycleBinOperationResult> PermanentlyDeleteAsync(string id, CancellationToken c) => throw new NotSupportedException();
        public Task<int> CleanupAsync(CancellationToken c) => throw new NotSupportedException();
        public Task<RecycleBinCleanupPreview> PreviewCleanupAsync(CancellationToken c) => throw new NotSupportedException();
    }

    private static LibraryItem Library(string id, string rootPath)
    {
        var now = DateTimeOffset.UtcNow;
        return new LibraryItem(
            id, "Test library", "movies", "main", rootPath, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }
}
