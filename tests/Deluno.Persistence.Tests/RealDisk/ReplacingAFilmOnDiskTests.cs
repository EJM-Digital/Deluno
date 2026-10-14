using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// An import that replaces a film already in the library, run on real files in a real temp folder through the real
/// import pipeline, the real recycle bin and a real SQLite catalogue. The old file is never lost: it is refused
/// before anything is touched, or it goes to the recycle bin where it can be restored, or the original is put back
/// byte for byte. Only the media probe is scripted (the bytes written are not real video).
/// </summary>
public sealed class ReplacingAFilmOnDiskTests
{
    private const string Release = "Sintel.2010.WEB.1080p";
    private const string BlockCatalogueWrite = "CREATE TRIGGER block_wanted_update BEFORE UPDATE ON movie_wanted_state BEGIN SELECT RAISE(ABORT, 'disk image is malformed'); END; CREATE TRIGGER block_wanted_insert BEFORE INSERT ON movie_wanted_state BEGIN SELECT RAISE(ABORT, 'disk image is malformed'); END;";

    private static async Task<RealDiskLibrary> StartWorldAsync(IMediaProbeService? probe = null)
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(probe));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static string TheOneFilmFile(RealDiskLibrary world)
        => Assert.Single(RealDiskLibrary.FilesUnder(world.Root), path => path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));

    private static void AssertNoImportLeftovers(RealDiskLibrary world)
    {
        var leftovers = RealDiskLibrary.FilesUnder(world.Root).Where(path => path.Contains(".deluno-", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("deluno-recycle-bin", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(leftovers.Length == 0, "staging, backup or marker files left behind: " + string.Join(", ", leftovers));
    }

    // ---------------------------------------------------------------- the replacement guard

    [Fact]
    public async Task A_replacement_with_a_lower_resolution_is_refused_and_the_existing_file_is_untouched()
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(() => ScriptedProbe.Good(1280, 720), () => ScriptedProbe.Good()));
        var (_, existing) = await world.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        var before = File.ReadAllBytes(existing);
        var incoming = world.WriteDownload(Release + ".mkv", fill: 0x22);

        var result = await world.ImportAsync(RealDiskLibrary.Request(incoming, "Sintel", 2010, overwrite: true, expectedExistingPath: existing));

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("replacementRejected", result.FailureKind);
        Assert.Contains("incoming video is 1280x720, existing video is 1920x1080", result.Message);
        Assert.Equal(before, File.ReadAllBytes(existing));
        Assert.Equal(existing, TheOneFilmFile(world));
        AssertNoImportLeftovers(world);
        Assert.True(File.Exists(incoming), "the refused download is left where it was");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0.91, false)]
    [InlineData(0.93, true)]
    public async Task A_replacement_whose_runtime_is_under_92_percent_of_the_existing_one_is_refused(double ratio, bool accepted)
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(() => ScriptedProbe.Good(seconds: 7200 * ratio), () => ScriptedProbe.Good(seconds: 7200)));
        var (_, existing) = await world.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        var incoming = world.WriteDownload(Release + ".mkv", fill: 0x22);

        var result = await world.ImportAsync(RealDiskLibrary.Request(incoming, "Sintel", 2010, overwrite: true, expectedExistingPath: existing));

        Assert.Equal(accepted, result.Succeeded);
        if (accepted)
        {
            Assert.All(File.ReadAllBytes(TheOneFilmFile(world)), value => Assert.Equal(0x22, value));
        }
        else
        {
            Assert.Equal(409, result.StatusCode);
            Assert.Contains("runtime is significantly shorter", result.Message);
            Assert.All(File.ReadAllBytes(existing), value => Assert.Equal(0x11, value));
        }
    }

    [Theory]
    [InlineData(0.64, false)]
    [InlineData(0.66, true)]
    public async Task A_replacement_whose_bitrate_is_under_65_percent_of_the_existing_one_is_refused(double ratio, bool accepted)
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(() => ScriptedProbe.Good(bitrate: (long)(12_000_000 * ratio)), () => ScriptedProbe.Good(bitrate: 12_000_000)));
        var (_, existing) = await world.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        var incoming = world.WriteDownload(Release + ".mkv", fill: 0x22);

        var result = await world.ImportAsync(RealDiskLibrary.Request(incoming, "Sintel", 2010, overwrite: true, expectedExistingPath: existing));

        Assert.Equal(accepted, result.Succeeded);
        if (!accepted)
        {
            Assert.Equal(409, result.StatusCode);
            Assert.Contains("bitrate is substantially lower", result.Message);
            Assert.All(File.ReadAllBytes(existing), value => Assert.Equal(0x11, value));
        }
    }

    [Fact]
    public async Task A_replacement_is_refused_when_the_existing_file_has_no_video_stream()
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(
            () => ScriptedProbe.Good(),
            () => ScriptedProbe.Good() with { VideoStreams = [] }));
        var (_, existing) = await world.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        var incoming = world.WriteDownload(Release + ".mkv", fill: 0x22);

        var result = await world.ImportAsync(RealDiskLibrary.Request(incoming, "Sintel", 2010, overwrite: true, expectedExistingPath: existing));

        Assert.Equal(409, result.StatusCode);
        Assert.Contains("missing a video stream", result.Message);
        Assert.All(File.ReadAllBytes(existing), value => Assert.Equal(0x11, value));
    }

    [Fact]
    public async Task A_replacement_whose_incoming_file_cannot_be_probed_is_refused_with_the_existing_file_intact()
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(
            () => ScriptedProbe.Good() with { Status = "unavailable", Message = "ffprobe is not installed" },
            () => ScriptedProbe.Good()));
        var (_, existing) = await world.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        var incoming = world.WriteDownload(Release + ".mkv", fill: 0x22);

        var result = await world.ImportAsync(RealDiskLibrary.Request(incoming, "Sintel", 2010, overwrite: true, expectedExistingPath: existing));

        Assert.Equal(409, result.StatusCode);
        Assert.Equal("replacementRejected", result.FailureKind);
        Assert.Contains("until the incoming file is successfully probed", result.Message);
        Assert.All(File.ReadAllBytes(existing), value => Assert.Equal(0x11, value));
        Assert.True(File.Exists(incoming));
    }

    // ---------------------------------------------------------------- the old file goes to the recycle bin

    [Fact]
    public async Task An_upgrade_to_a_differently_named_file_puts_the_old_file_and_its_subtitle_in_the_recycle_bin_where_restore_gives_them_back()
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(() => ScriptedProbe.Good(), () => ScriptedProbe.Good()));
        var (movie, oldFile) = await world.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        var oldSubtitle = Path.ChangeExtension(oldFile, ".en.srt");
        File.WriteAllText(oldSubtitle, "1\n00:00:01,000 --> 00:00:02,000\nold subtitle\n");

        var newSource = world.WriteDownload("Sintel.2010.BluRay.1080p.mkv", fill: 0x22);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 BluRay 1080p");
        var result = await world.ImportAsync(RealDiskLibrary.Request(newSource, "Sintel", 2010, overwrite: true, forceReplacement: true, dispatchId: dispatch, expectedExistingPath: oldFile));

        Assert.True(result.Succeeded, result.Message);
        var newFile = result.Response!.Preview.DestinationPath;
        Assert.NotEqual(oldFile, newFile);
        Assert.All(File.ReadAllBytes(newFile), value => Assert.Equal(0x22, value));
        Assert.False(File.Exists(oldFile), "the old film left the library folder");
        Assert.False(File.Exists(oldSubtitle), "the old subtitle left with it");
        Assert.Equal(newFile, TheOneFilmFile(world));

        var items = await world.Bin.ListAsync(CancellationToken.None);
        Assert.Equal(2, items.Count);
        var videoItem = Assert.Single(items, item => item.OriginalPath == oldFile);
        Assert.True(File.Exists(videoItem.RecyclePath));
        Assert.All(File.ReadAllBytes(videoItem.RecyclePath), value => Assert.Equal(0x11, value));
        Assert.Single(items, item => item.OriginalPath == oldSubtitle);

        var restored = await world.Bin.RestoreAsync(videoItem.Id, CancellationToken.None);
        Assert.True(restored.Success, restored.Message);
        Assert.All(File.ReadAllBytes(oldFile), value => Assert.Equal(0x11, value));
        AssertNoImportLeftovers(world);
    }

    [Fact]
    public async Task An_upgrade_onto_the_same_path_places_the_new_file_and_hands_the_old_bytes_to_the_recycle_bin_with_no_backup_left()
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(() => ScriptedProbe.Good(), () => ScriptedProbe.Good()));
        var (movie, oldFile) = await world.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        var newSource = world.WriteDownload(Release + ".mkv", fill: 0x22);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p REPACK");

        var result = await world.ImportAsync(RealDiskLibrary.Request(newSource, "Sintel", 2010, overwrite: true, forceReplacement: true, dispatchId: dispatch, expectedExistingPath: oldFile));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(oldFile, result.Response!.Preview.DestinationPath);
        Assert.All(File.ReadAllBytes(oldFile), value => Assert.Equal(0x22, value));
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.All(File.ReadAllBytes(item.RecyclePath), value => Assert.Equal(0x11, value));
        AssertNoImportLeftovers(world);
    }

    [Fact]
    public async Task An_old_file_outside_every_library_root_is_never_moved_or_deleted_by_an_upgrade()
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(() => ScriptedProbe.Good(), () => ScriptedProbe.Good()));
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var outsideFolder = Path.Combine(world.App.DataRoot, "somewhere-else");
        Directory.CreateDirectory(outsideFolder);
        var outsideFile = Path.Combine(outsideFolder, "Sintel.2010.DVD.mkv");
        File.WriteAllBytes(outsideFile, Enumerable.Repeat((byte)0x77, 4096).ToArray());
        await world.Movies.ImportExistingAsync(world.Library.Id, "Sintel", 2010, "upgrade", "test", "DVD", null, false, outsideFile, 4096, CancellationToken.None, movieId: movie.Id);

        var source = world.WriteDownload(Release + ".mkv", fill: 0x22);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");
        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, overwrite: true, forceReplacement: true, dispatchId: dispatch, expectedExistingPath: outsideFile));

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(outsideFile), "a file outside every library root is never touched");
        Assert.All(File.ReadAllBytes(outsideFile), value => Assert.Equal(0x77, value));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Single(RealDiskLibrary.FilesUnder(world.Root), path => path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task When_the_recycle_bin_cannot_take_the_old_file_it_is_left_in_place_and_the_import_still_lands()
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(() => ScriptedProbe.Good(), () => ScriptedProbe.Good()));
        var (movie, oldFile) = await world.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        // A plain file sits where the holding folder must be created, so the bin cannot make room.
        File.WriteAllText(world.Root + ".deluno-recycle-bin", "not a folder");

        var newSource = world.WriteDownload("Sintel.2010.BluRay.1080p.mkv", fill: 0x22);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 BluRay 1080p");
        var result = await world.ImportAsync(RealDiskLibrary.Request(newSource, "Sintel", 2010, overwrite: true, forceReplacement: true, dispatchId: dispatch, expectedExistingPath: oldFile));

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(oldFile), "the old film stays where it was when the bin cannot take it");
        Assert.All(File.ReadAllBytes(oldFile), value => Assert.Equal(0x11, value));
        Assert.All(File.ReadAllBytes(result.Response!.Preview.DestinationPath), value => Assert.Equal(0x22, value));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    // ---------------------------------------------------------------- a move import

    [Fact]
    public async Task A_move_import_leaves_the_bytes_in_the_library_and_nothing_in_the_downloads_folder_or_staging()
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(() => ScriptedProbe.Good(), () => ScriptedProbe.Good()));
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv", fill: 0x44);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, mode: "move", dispatchId: dispatch));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("move", result.Response!.TransferModeUsed);
        Assert.Contains("using move", result.Message);
        Assert.All(File.ReadAllBytes(TheOneFilmFile(world)), value => Assert.Equal(0x44, value));
        Assert.False(File.Exists(source), "a move leaves nothing behind in the downloads folder");
        AssertNoImportLeftovers(world);
        Assert.True((await world.Movies.GetByIdAsync(movie.Id, CancellationToken.None))!.HasFile);
    }

    // ---------------------------------------------------------------- a failure after the file was placed puts everything back

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task A_catalogue_write_that_fails_after_the_file_was_placed_removes_the_new_file_and_keeps_the_source_and_opens_a_recovery_case(string mode)
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(() => ScriptedProbe.Good(), () => ScriptedProbe.Good()));
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv", fill: 0x33);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");
        await world.SqlAsync("movies", BlockCatalogueWrite);

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, mode: mode, dispatchId: dispatch));

        Assert.False(result.Succeeded);
        Assert.Equal(500, result.StatusCode);
        Assert.DoesNotContain(RealDiskLibrary.FilesUnder(world.Root), path => path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(source), "the download is still where it was (a move put back)");
        Assert.All(File.ReadAllBytes(source), value => Assert.Equal(0x33, value));
        AssertNoImportLeftovers(world);
        Assert.NotEmpty((await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
    }

    [Fact]
    public async Task A_catalogue_write_that_fails_after_an_upgrade_onto_the_same_path_gives_the_original_bytes_back_exactly()
    {
        await using var world = await StartWorldAsync(ScriptedProbe.ByFolder(() => ScriptedProbe.Good(), () => ScriptedProbe.Good()));
        var (movie, oldFile) = await world.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        var original = File.ReadAllBytes(oldFile);
        var newSource = world.WriteDownload(Release + ".mkv", fill: 0x22);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p REPACK");
        await world.SqlAsync("movies", BlockCatalogueWrite);

        var result = await world.ImportAsync(RealDiskLibrary.Request(newSource, "Sintel", 2010, overwrite: true, forceReplacement: true, dispatchId: dispatch, expectedExistingPath: oldFile));

        Assert.False(result.Succeeded);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal(original, File.ReadAllBytes(oldFile));
        Assert.Equal(oldFile, TheOneFilmFile(world));
        AssertNoImportLeftovers(world);
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        Assert.True(File.Exists(newSource));
    }
}
