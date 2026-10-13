using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Contracts;
using Deluno.Platform;
using Deluno.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Platform.Tests;

/// <summary>
/// #1260: the recycle bin moves the files first and writes its list second. A list that cannot be written (a backup, a
/// scanner or a reader holding the old one) used to throw straight away with the items already moved and not recorded; a list
/// that is busy or cut short must never be read as "empty" and written back.
/// </summary>
public sealed class RecycleBinStateFileTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"deluno-recycle-state-{Guid.NewGuid():N}");
    private readonly ScriptedStateFileSystem _io = new();

    private string DataRoot => Path.Combine(_sandbox, "data");
    private string StatePath => Path.Combine(DataRoot, "recycle-bin", "state.json");

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private RecycleBinService Service(IStateFileSystem? io = null)
        => new(
            Options.Create(new StoragePathOptions { DataRoot = DataRoot }),
            TimeProvider.System,
            NullLogger<RecycleBinService>.Instance,
            stateFiles: io);

    private (string Library, string Movie) Movie(string name = "Example.Movie.2026.mkv")
    {
        var library = Path.Combine(_sandbox, "library");
        Directory.CreateDirectory(library);
        var movie = Path.Combine(library, name);
        File.WriteAllText(movie, "movie");
        return (library, movie);
    }

    private static LibraryItem Library(string root)
    {
        var now = DateTimeOffset.UtcNow;
        return new LibraryItem(
            "library-1", "Test library", "movies", "main", root, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    [Fact]
    public async Task A_busy_state_file_is_waited_for_and_the_moved_item_is_recorded()
    {
        var (library, movie) = Movie();
        // The list's rename is refused three times, as it is while a backup or a scanner holds the old file.
        _io.MoveFault = (to, attempt) => to.EndsWith("state.json", StringComparison.Ordinal) && attempt <= 3 ? ScriptedStateFileSystem.SharingViolation() : null;
        var service = Service(_io);

        var moved = await service.MoveAsync([new TrackedLibraryFile("library-1", movie)], [Library(library)], CancellationToken.None);

        Assert.Single(moved.Items);
        Assert.Single(await Service().ListAsync(CancellationToken.None));
        Assert.True(_io.Sleeps.Count >= 3);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StatePath)!, "*.tmp"));
    }

    [Fact]
    public async Task A_state_file_that_cannot_be_written_for_the_whole_wait_says_so_instead_of_losing_the_record()
    {
        var (library, movie) = Movie();
        _io.MoveFault = (to, _) => to.EndsWith("state.json", StringComparison.Ordinal) ? ScriptedStateFileSystem.AccessDenied() : null;

        var failure = await Assert.ThrowsAsync<StateFileException>(
            () => Service(_io).MoveAsync([new TrackedLibraryFile("library-1", movie)], [Library(library)], CancellationToken.None));

        Assert.Contains("could not be saved", failure.Message);
        Assert.Equal(StateFile.WriteAttempts - 1, _io.Sleeps.Count(pause => pause == StateFile.WritePause));
    }

    [Fact]
    public async Task A_busy_state_file_is_never_read_as_an_empty_bin()
    {
        var (library, movie) = Movie();
        await Service().MoveAsync([new TrackedLibraryFile("library-1", movie)], [Library(library)], CancellationToken.None);
        var before = File.ReadAllText(StatePath);
        _io.ReadFault = (path, _) => path.EndsWith("state.json", StringComparison.Ordinal) ? ScriptedStateFileSystem.SharingViolation() : null;

        var failure = await Assert.ThrowsAsync<StateFileException>(() => Service(_io).ListAsync(CancellationToken.None));

        Assert.Contains("in use", failure.Message);
        Assert.Equal(before, File.ReadAllText(StatePath));
    }

    [Fact]
    public async Task A_cut_short_state_file_is_kept_and_never_written_over_by_the_next_move()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, "{\"settings\":{\"retentionDays\":30},\"items\":[{\"id\":\"ab");
        var (library, movie) = Movie();

        var failure = await Assert.ThrowsAsync<StateFileException>(
            () => Service().MoveAsync([new TrackedLibraryFile("library-1", movie)], [Library(library)], CancellationToken.None));

        Assert.Contains("damaged", failure.Message);
        Assert.StartsWith("{\"settings\"", File.ReadAllText(StatePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StatePath)!, "state.json.unreadable-*"));
    }

    [Fact]
    public async Task A_missing_state_file_is_an_empty_bin()
    {
        Assert.Empty(await Service(_io).ListAsync(CancellationToken.None));
        Assert.True(File.Exists(StatePath));
    }
}
