using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// An import that a restart interrupted, put right by the real recovery service on a real temp folder with the real
/// import pipeline, the real recycle bin and the real health and activity stores. The staged file is the only copy
/// of the media in the worst case, so it is finished, put back, or kept; it is never lost.
/// </summary>
public sealed class RestartRecoveryOnDiskTests
{
    private const string ReleaseFile = "Sanctuary.2024.WEB.1080p.mkv";

    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private sealed record Scene(string Source, string Destination, string Staging, string Marker, byte[] Bytes);

    private static readonly byte[] Content = Enumerable.Range(0, 4096).Select(value => (byte)((value * 7) % 251)).ToArray();

    /// <summary>What a crash leaves: a staging file and its marker. A move consumed the original; a copy left it; a hardlink shares its data.</summary>
    private static Scene Lay(RealDiskLibrary world, string mode, string? markerText = null, bool stagingPresent = true, bool writeMarker = true)
    {
        var folder = Path.Combine(world.Root, "Sanctuary (2024)");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "Sanctuary (2024).mkv");
        var staging = destination + ".deluno-stage-restarttest.tmp";
        var source = Path.Combine(world.Downloads, ReleaseFile);
        if (stagingPresent)
        {
            switch (mode)
            {
                case "move":
                    File.WriteAllBytes(staging, Content);
                    break;
                case "copy":
                    File.WriteAllBytes(source, Content);
                    File.WriteAllBytes(staging, Content);
                    break;
                default:
                    File.WriteAllBytes(source, Content);
                    RealHardLink.Create(source, staging);
                    break;
            }
        }
        else if (mode != "move")
        {
            File.WriteAllBytes(source, Content);
        }

        foreach (var file in new[] { source, staging }.Where(File.Exists))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-5));
        }

        var marker = ImportRestartMarkerFile.PathFor(staging);
        if (writeMarker)
        {
            if (markerText is null)
            {
                Assert.Null(ImportRestartMarkerFile.WriteAtomic(marker, new ImportRestartMarker(source, destination, staging, "movies", "Sanctuary (2024)")));
            }
            else
            {
                File.WriteAllText(marker, markerText);
            }
        }

        return new Scene(source, destination, staging, marker, Content);
    }

    private static async Task<ImportRestartRecoveryReport> RecoverAsync(RealDiskLibrary world, bool withRecycleBin = true)
    {
        using var scope = world.App.Services.CreateScope();
        var services = scope.ServiceProvider;
        var service = withRecycleBin
            ? services.GetRequiredService<IImportRestartRecoveryService>()
            : new ImportRestartRecoveryService(
                services.GetRequiredService<ILibrariesRepository>(),
                services.GetRequiredService<IImportPipelineService>(),
                services.GetRequiredService<IActivityFeedRepository>(),
                NullLogger<ImportRestartRecoveryService>.Instance,
                recycleBinService: null,
                healthChecks: services.GetRequiredService<IHealthCheckRepository>());
        return await service.RecoverAsync(CancellationToken.None);
    }

    private static string[] VideosInLibrary(RealDiskLibrary world)
        => RealDiskLibrary.FilesUnder(world.Root).Where(path => path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static async Task<IReadOnlyList<ActivityEventItem>> ActivityAsync(RealDiskLibrary world)
        => await world.App.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, null, null, CancellationToken.None);

    // ---------------------------------------------------------------- a good marker

    [Fact]
    public async Task A_move_import_with_only_its_staged_file_left_gets_the_media_put_back_and_imported_as_a_fresh_download()
    {
        await using var world = await StartWorldAsync();
        var scene = Lay(world, "move");

        var report = await RecoverAsync(world);

        Assert.Equal(1, report.Recovered);
        Assert.False(File.Exists(scene.Staging));
        Assert.False(File.Exists(scene.Marker));
        var imported = Assert.Single(VideosInLibrary(world));
        Assert.Equal(scene.Bytes, File.ReadAllBytes(imported));
        Assert.Contains(await ActivityAsync(world), entry => entry.Category == "filesystem.import.restart-recovered");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("hardlink")]
    public async Task A_copy_or_hardlink_import_with_its_staging_file_present_drops_only_the_redundant_staging_copy_and_imports_the_original(string mode)
    {
        await using var world = await StartWorldAsync();
        var scene = Lay(world, mode);

        var report = await RecoverAsync(world);

        Assert.Equal(1, report.Recovered);
        Assert.False(File.Exists(scene.Staging), "only the extra staging copy was dropped");
        Assert.False(File.Exists(scene.Marker));
        Assert.Equal(scene.Bytes, File.ReadAllBytes(Assert.Single(VideosInLibrary(world))));
        // The original download was the media: it was imported from, not deleted by the recovery.
        Assert.Equal(scene.Bytes, File.ReadAllBytes(scene.Source));
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("hardlink")]
    public async Task A_copy_or_hardlink_import_whose_staging_file_is_gone_imports_from_the_original(string mode)
    {
        await using var world = await StartWorldAsync();
        var scene = Lay(world, mode, stagingPresent: false);

        var report = await RecoverAsync(world);

        Assert.Equal(1, report.Recovered);
        Assert.False(File.Exists(scene.Marker));
        Assert.Equal(scene.Bytes, File.ReadAllBytes(Assert.Single(VideosInLibrary(world))));
        Assert.Equal(scene.Bytes, File.ReadAllBytes(scene.Source));
    }

    // ---------------------------------------------------------------- a damaged marker never costs the media

    [Theory]
    [InlineData("{\"SourcePath\":\"C:\\\\downloads\\\\Sanct")]
    [InlineData("")]
    [InlineData("this is just a sentence, not a restart marker")]
    public async Task A_truncated_empty_or_non_marker_file_sends_the_staging_file_to_the_recycle_bin_and_tells_the_person(string markerText)
    {
        await using var world = await StartWorldAsync();
        var scene = Lay(world, "move", markerText);

        var report = await RecoverAsync(world);

        Assert.Equal(1, report.Abandoned);
        Assert.False(File.Exists(scene.Staging));
        Assert.False(File.Exists(scene.Marker));
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(scene.Bytes, File.ReadAllBytes(item.RecyclePath));
        Assert.Equal(scene.Staging, item.OriginalPath);
        Assert.Contains(await ActivityAsync(world), entry => entry.Category == "filesystem.import.restart-media-kept" && entry.Message.Contains("recycle bin"));
        var notes = await world.App.Services.GetRequiredService<IHealthCheckRepository>().ListAsync(CancellationToken.None);
        Assert.Contains(notes, note => note.Id.StartsWith("notice:import-restart-media:", StringComparison.Ordinal));
        Assert.Empty(VideosInLibrary(world));
    }

    [Fact]
    public async Task A_damaged_marker_with_no_recycle_bin_leaves_the_media_where_it_is_and_says_so()
    {
        await using var world = await StartWorldAsync();
        var scene = Lay(world, "move", markerText: "");

        var report = await RecoverAsync(world, withRecycleBin: false);

        Assert.Equal(1, report.Abandoned);
        Assert.True(File.Exists(scene.Staging), "with no bin the media stays exactly where it is");
        Assert.Equal(scene.Bytes, File.ReadAllBytes(scene.Staging));
        Assert.False(File.Exists(scene.Marker));
        Assert.Contains(await ActivityAsync(world), entry => entry.Category == "filesystem.import.restart-media-kept" && entry.Message.Contains("left the file where it is"));
    }

    [Fact]
    public async Task A_damaged_marker_for_a_file_that_is_gone_is_just_removed()
    {
        await using var world = await StartWorldAsync();
        var scene = Lay(world, "move", markerText: "{\"Source", stagingPresent: false);

        var report = await RecoverAsync(world);

        Assert.Equal(1, report.Abandoned);
        Assert.False(File.Exists(scene.Marker));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        Assert.DoesNotContain(await ActivityAsync(world), entry => entry.Category == "filesystem.import.restart-media-kept");
    }

    // ---------------------------------------------------------------- the destination already holds a file

    [Fact]
    public async Task When_the_destination_already_holds_the_file_and_no_staging_file_is_left_the_marker_is_just_cleaned()
    {
        await using var world = await StartWorldAsync();
        var scene = Lay(world, "move", stagingPresent: false);
        File.WriteAllBytes(scene.Destination, scene.Bytes);

        var report = await RecoverAsync(world);

        Assert.Equal(1, report.Recovered);
        Assert.False(File.Exists(scene.Marker));
        Assert.Equal(scene.Bytes, File.ReadAllBytes(scene.Destination));
        Assert.Contains(await ActivityAsync(world), entry => entry.Category == "filesystem.import.restart-recovered" && entry.Message.Contains("already in its library folder"));
    }

    [Fact]
    public async Task When_the_destination_holds_the_same_size_the_extra_staging_copy_is_deleted()
    {
        await using var world = await StartWorldAsync();
        var scene = Lay(world, "move");
        File.WriteAllBytes(scene.Destination, scene.Bytes);

        var report = await RecoverAsync(world);

        Assert.Equal(1, report.Recovered);
        Assert.False(File.Exists(scene.Staging));
        Assert.False(File.Exists(scene.Marker));
        Assert.Equal(scene.Bytes, File.ReadAllBytes(scene.Destination));
    }

    [Fact]
    public async Task A_different_file_already_at_the_destination_keeps_the_staging_file_in_the_recycle_bin_and_tells_the_person()
    {
        await using var world = await StartWorldAsync();
        var scene = Lay(world, "move");
        var other = Enumerable.Repeat((byte)0x77, 999).ToArray();
        File.WriteAllBytes(scene.Destination, other);

        var report = await RecoverAsync(world);

        Assert.Equal(1, report.Abandoned);
        Assert.Equal(other, File.ReadAllBytes(scene.Destination));
        Assert.False(File.Exists(scene.Staging));
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(scene.Bytes, File.ReadAllBytes(item.RecyclePath));
        Assert.Contains(await ActivityAsync(world), entry => entry.Category == "filesystem.import.restart-media-kept" && entry.Message.Contains("different file was already in the library folder"));
    }
}
