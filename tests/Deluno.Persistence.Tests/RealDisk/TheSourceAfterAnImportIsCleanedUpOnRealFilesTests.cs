using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// "Remove the source after import" on a real folder (coverage register, engines): after a hardlink import the download's name goes and the
/// library's name for the same data stays with every byte; the folders the download leaves empty are removed up to, but never including,
/// the downloads folder; and a folder that still holds something is kept. Refs #1296.
/// </summary>
public sealed class TheSourceAfterAnImportIsCleanedUpOnRealFilesTests
{
    private const string Release = "Sintel.2010.1080p.WEB-DL.x264-GRP";

    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        var world = await RealDiskLibrary.OpenAsync(app);
        await ApiScenes.ExecuteAsync(app, "platform", "UPDATE libraries SET cleanup_mode = 'remove-source-after-import', remove_empty_source_folders = 1;");
        return world;
    }

    [Fact]
    public async Task A_hardlink_import_removes_the_downloads_name_and_keeps_the_library_file_whole_and_the_empty_folders_go_up_to_the_downloads_folder()
    {
        await using var world = await StartWorldAsync();
        await world.AddMovieAsync("Sintel", 2010);
        var folder = Directory.CreateDirectory(Path.Combine(world.Downloads, "Packs", Release)).FullName;
        var source = Path.Combine(folder, Release + ".mkv");
        File.WriteAllBytes(source, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-5));
        var original = File.ReadAllBytes(source);

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, mode: "hardlink"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("hardlink", result.Response!.TransferModeUsed);
        var library = result.Response.Preview.DestinationPath;
        Assert.False(File.Exists(source), "the download's name is gone");
        Assert.Equal(original, File.ReadAllBytes(library));
        Assert.False(Directory.Exists(folder), "the folder the download left empty is gone");
        Assert.False(Directory.Exists(Path.Combine(world.Downloads, "Packs")), "so is the folder above it that is now empty");
        Assert.True(Directory.Exists(world.Downloads), "the downloads folder itself is never removed");
    }

    [Fact]
    public async Task A_source_folder_that_still_holds_a_file_is_kept()
    {
        await using var world = await StartWorldAsync();
        await world.AddMovieAsync("Sintel", 2010);
        var folder = Directory.CreateDirectory(Path.Combine(world.Downloads, Release)).FullName;
        var source = Path.Combine(folder, Release + ".mkv");
        File.WriteAllBytes(source, Enumerable.Repeat((byte)0x31, 4096).ToArray());
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-5));
        var other = Path.Combine(folder, "Sample.txt");
        File.WriteAllText(other, "left behind");

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, mode: "copy"));

        Assert.True(result.Succeeded, result.Message);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(other), "a file that is not the import's own stays");
        Assert.True(Directory.Exists(folder), "a folder that still holds a file is not removed");
    }
}
