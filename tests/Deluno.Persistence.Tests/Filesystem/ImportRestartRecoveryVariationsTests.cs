using Deluno.Contracts;
using Deluno.Filesystem;
using static Deluno.Persistence.Tests.Filesystem.ImportRestartRecoveryKeepsMediaTests;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #1259: the rest of the variations. A marker is told apart as busy (never touched, tried again), damaged (the
/// media is kept and the person told) or missing; the staging file is present or missing; and the marker is written
/// whole or not at all.
/// </summary>
public sealed class ImportRestartRecoveryVariationsTests
{
    public static TheoryData<string> Modes => ImportRestartRecoveryKeepsMediaTests.Modes;

    /// <summary>Wraps real file reads and fails the first few, the way another program holding the marker does.</summary>
    private sealed class FlakyIo(Func<int, Exception?> failRead, Func<int, Exception?>? failMove = null, Func<int, Exception?>? failWrite = null) : IImportRestartMarkerIo
    {
        public int Reads { get; private set; }

        public int Moves { get; private set; }

        public int Writes { get; private set; }

        public int Sleeps { get; private set; }

        public bool Exists(string path) => File.Exists(path);

        public string ReadAllText(string path)
        {
            Reads++;
            if (failRead(Reads) is { } error)
            {
                throw error;
            }

            return File.ReadAllText(path);
        }

        public void WriteAllText(string path, string text)
        {
            Writes++;
            if (failWrite?.Invoke(Writes) is { } error)
            {
                throw error;
            }

            File.WriteAllText(path, text);
        }

        public void Move(string from, string to)
        {
            Moves++;
            if (failMove?.Invoke(Moves) is { } error)
            {
                throw error;
            }

            File.Move(from, to, overwrite: true);
        }

        public void Sleep(TimeSpan pause) => Sleeps++;
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_marker_that_is_access_denied_never_costs_the_staging_file(string mode)
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay(mode, markerText: null);
        var io = new FlakyIo(_ => new UnauthorizedAccessException("access denied: a delete is pending"));

        var report = await rig.CreateService(io).RecoverAsync(CancellationToken.None);

        Assert.True(File.Exists(scene.StagingPath));
        Assert.True(File.Exists(scene.MarkerPath));
        Assert.Equal(new ImportRestartRecoveryReport(0, 0, 1), report);
        Assert.Empty(rig.Pipeline.Imports);
        Assert.Empty(await rig.Bin.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_busy_marker_is_tried_a_bounded_number_of_times_and_then_left_for_the_next_pass()
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: null);
        var io = new FlakyIo(_ => new IOException("sharing violation"));

        await rig.CreateService(io).RecoverAsync(CancellationToken.None);

        // Two passes over the one marker (the sweep, then the single second look), each bounded.
        Assert.Equal(2 * 26, io.Reads);
        Assert.True(File.Exists(scene.StagingPath));
        Assert.True(File.Exists(scene.MarkerPath));
    }

    [Fact]
    public async Task A_marker_released_during_the_pass_is_recovered_without_a_restart()
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: null);
        // Held through the whole first look (26 tries), free for the second.
        var io = new FlakyIo(read => read <= 26 ? new IOException("sharing violation") : null);

        var report = await rig.CreateService(io).RecoverAsync(CancellationToken.None);

        Assert.Equal(new ImportRestartRecoveryReport(1, 0, 0), report);
        Assert.Single(rig.Pipeline.Imports);
        Assert.Equal(scene.SourcePath, rig.Pipeline.Imports[0].Preview.SourcePath);
        Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.SourcePath));
        Assert.False(File.Exists(scene.MarkerPath));
    }

    [Fact]
    public async Task A_marker_busy_for_a_moment_is_read_after_a_short_wait()
    {
        using var rig = await RestartRig.CreateAsync();
        rig.Lay("copy", markerText: null);
        var io = new FlakyIo(read => read <= 3 ? new IOException("sharing violation") : null);

        var report = await rig.CreateService(io).RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        Assert.Equal(3, io.Sleeps);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_damaged_marker_sends_the_staging_file_to_the_recycle_bin_and_tells_the_person(string mode)
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay(mode, markerText: "{\"SourcePath\":\"C:\\\\down");

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Abandoned);
        Assert.False(File.Exists(scene.StagingPath));
        var item = Assert.Single(await rig.Bin.ListAsync(CancellationToken.None));
        Assert.NotNull(item);
        Assert.False(File.Exists(scene.MarkerPath), "only once the media is safe, the damaged marker goes");
        Assert.Empty(rig.Pipeline.Imports);

        var activity = await rig.Jobs.ListActivityAsync(50, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Category == "filesystem.import.restart-media-kept"
            && entry.Message.Contains("recycle bin", StringComparison.Ordinal));
        var problem = Assert.Single(await rig.Health.ListAsync(CancellationToken.None));
        Assert.Equal(HealthCheckStatuses.Problem, problem.Status);
        Assert.Contains("recycle bin", problem.Message, StringComparison.Ordinal);
        if (mode != "move")
        {
            Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.SourcePath));
        }
    }

    [Fact]
    public async Task A_damaged_marker_with_no_recycle_bin_leaves_the_media_where_it_is()
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: "not json at all");

        await rig.CreateService(withRecycleBin: false).RecoverAsync(CancellationToken.None);

        Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.StagingPath));
        var problem = Assert.Single(await rig.Health.ListAsync(CancellationToken.None));
        Assert.Contains(scene.StagingPath, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_damaged_marker_for_a_file_that_is_gone_is_just_removed()
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: "{", stagingPresent: false);

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Abandoned);
        Assert.False(File.Exists(scene.MarkerPath));
        Assert.Empty(await rig.Bin.ListAsync(CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_staging_file_with_no_marker_is_left_alone(string mode)
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay(mode, markerText: null, writeMarker: false);

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(new ImportRestartRecoveryReport(0, 0, 0), report);
        Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.StagingPath));
    }

    [Fact]
    public async Task A_move_import_with_a_good_marker_gets_its_media_back_in_place_and_imported()
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: null);

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(new ImportRestartRecoveryReport(1, 0, 0), report);
        Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.SourcePath));
        Assert.False(File.Exists(scene.StagingPath));
        Assert.False(File.Exists(scene.MarkerPath));
        Assert.Equal(scene.SourcePath, Assert.Single(rig.Pipeline.Imports).Preview.SourcePath);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("hardlink")]
    public async Task A_copy_or_hardlink_import_with_a_good_marker_drops_only_the_extra_staging_file(string mode)
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay(mode, markerText: null);

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        Assert.False(File.Exists(scene.StagingPath));
        Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.SourcePath));
        Assert.Single(rig.Pipeline.Imports);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("hardlink")]
    public async Task A_copy_or_hardlink_import_whose_staging_file_is_gone_imports_from_the_original(string mode)
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay(mode, markerText: null, stagingPresent: false);

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.SourcePath));
        Assert.Single(rig.Pipeline.Imports);
    }

    [Fact]
    public async Task A_move_import_whose_staging_file_is_gone_has_nothing_to_protect()
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: null, stagingPresent: false);

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Abandoned);
        Assert.False(File.Exists(scene.MarkerPath));
        Assert.Empty(rig.Pipeline.Imports);
        var activity = await rig.Jobs.ListActivityAsync(50, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Category == "filesystem.import.restart-abandoned");
    }

    [Fact]
    public async Task A_different_file_already_in_the_library_does_not_make_the_staging_file_disposable()
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: null);
        await File.WriteAllBytesAsync(scene.DestinationPath, [1, 2, 3]);

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Abandoned);
        Assert.False(File.Exists(scene.StagingPath));
        Assert.Single(await rig.Bin.ListAsync(CancellationToken.None));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(scene.DestinationPath));
        Assert.Single(await rig.Health.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_recovery_keeps_the_media()
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: null);
        // The place the original used to be is a file, so the media cannot be put back there.
        Directory.CreateDirectory(Path.GetDirectoryName(scene.SourcePath)!);
        Directory.Delete(Path.GetDirectoryName(scene.SourcePath)!, recursive: true);
        await File.WriteAllTextAsync(Path.GetDirectoryName(scene.SourcePath)!, "a file where the downloads folder should be");

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Abandoned);
        Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.StagingPath));
        Assert.True(File.Exists(scene.MarkerPath), "the marker stays so the next recovery tries again");
        Assert.Single(await rig.Health.ListAsync(CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Recovery_leaves_an_import_that_is_running_right_now_alone(string mode)
    {
        using var rig = await RestartRig.CreateAsync();
        // Truncated on purpose: a marker being written, or one a crash left, looks just like this mid-import.
        var scene = rig.Lay(mode, markerText: "{\"Sourc");
        ImportRestartMarkerFile.MarkInFlight(scene.StagingPath);
        try
        {
            var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

            Assert.Equal(new ImportRestartRecoveryReport(0, 0, 1), report);
            Assert.True(File.Exists(scene.StagingPath));
            Assert.True(File.Exists(scene.MarkerPath));
            Assert.Empty(rig.Pipeline.Imports);
        }
        finally
        {
            ImportRestartMarkerFile.ClearInFlight(scene.StagingPath);
        }
    }

    [Fact]
    public async Task The_marker_is_written_whole_and_reads_back()
    {
        using var rig = await RestartRig.CreateAsync();
        var path = Path.Combine(rig.Root, "x.mkv.deluno-stage-1.tmp.deluno-marker.json");
        var marker = new ImportRestartMarker("a", "b", "c", "movies", "T");

        Assert.Null(ImportRestartMarkerFile.WriteAtomic(path, marker));

        var read = ImportRestartMarkerFile.Read(path);
        Assert.Equal(ImportRestartMarkerKind.Ok, read.Kind);
        Assert.Equal(marker, read.Marker);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task A_write_that_dies_halfway_leaves_the_old_marker_whole()
    {
        using var rig = await RestartRig.CreateAsync();
        var path = Path.Combine(rig.Root, "y.mkv.deluno-stage-1.tmp.deluno-marker.json");
        var old = new ImportRestartMarker("old-a", "old-b", "old-c", "movies", "Old");
        Assert.Null(ImportRestartMarkerFile.WriteAtomic(path, old));
        // The temporary file is written, then the rename is refused every time.
        var io = new FlakyIo(_ => null, failMove: _ => new UnauthorizedAccessException("access denied"));

        var error = ImportRestartMarkerFile.WriteAtomic(path, new ImportRestartMarker("n", "n", "n", "movies", "New"), io);

        Assert.NotNull(error);
        Assert.Equal(old, ImportRestartMarkerFile.Read(path).Marker);
    }

    [Fact]
    public async Task A_busy_marker_write_is_tried_again_and_lands()
    {
        using var rig = await RestartRig.CreateAsync();
        var path = Path.Combine(rig.Root, "z.mkv.deluno-stage-1.tmp.deluno-marker.json");
        var io = new FlakyIo(_ => null, failMove: move => move <= 3 ? new IOException("sharing violation") : null);

        var error = ImportRestartMarkerFile.WriteAtomic(path, new ImportRestartMarker("a", "b", "c", "movies", "T"), io);

        Assert.Null(error);
        Assert.Equal(4, io.Moves);
        Assert.Equal(ImportRestartMarkerKind.Ok, ImportRestartMarkerFile.Read(path).Kind);
    }
}
