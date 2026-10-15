using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Library folders on a real temp folder: the folder a library may point at, the title folders the bin moves and
/// skips, and what the person is told when a whole-folder move cannot finish on disk or in the records.
/// </summary>
public sealed class LibraryFoldersOnDiskTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static string Write(string path, string text = "not really a film")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static async Task<LibraryItem> LibraryOnAsync(RealDiskLibrary world, string name, string root)
    {
        Directory.CreateDirectory(root);
        var response = await world.App.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name, mediaType = "movies", purpose = "collection", rootPath = root, downloadsPath = (string?)null, qualityProfileId = (string?)null,
            importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await world.App.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single(item => item.Name == name);
    }

    // ---------------------------------------------------------------- the folder a library points at

    [Theory]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("blank")]
    public async Task Changing_a_librarys_folder_to_one_that_does_not_exist_a_file_or_blank_is_refused_and_changes_nothing(string kind)
    {
        await using var world = await StartWorldAsync();
        var before = world.Library;
        var target = kind switch
        {
            "missing" => Path.Combine(world.App.DataRoot, "no-such-folder"),
            "file" => Write(Path.Combine(world.App.DataRoot, "a-file.txt")),
            _ => "  "
        };

        var response = await world.App.Client.PutAsJsonAsync($"/api/libraries/{before.Id}", new { name = before.Name, rootPath = target });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(kind switch { "missing" => "does not exist", "file" => "is a file, not a folder", _ => "Choose a folder for this library" }, body);
        var after = (await world.App.Services.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None)).Single();
        Assert.Equal(before.RootPath, after.RootPath);
        Assert.Equal(before.Name, after.Name);
        Assert.True(Directory.Exists(world.Root));
    }

    // ---------------------------------------------------------------- what the bin moves and skips

    [Fact]
    public async Task Moving_to_the_bin_skips_a_tracked_path_outside_the_library_root_and_a_library_with_no_root_with_a_warning_and_touches_neither()
    {
        await using var world = await StartWorldAsync();
        var inside = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu.mkv"));
        var outside = Write(Path.Combine(world.App.DataRoot, "elsewhere", "Other (2000)", "Other.mkv"));

        var result = await world.Bin.MoveAsync(
            [new TrackedLibraryFile(world.Library.Id, outside), new TrackedLibraryFile("a-library-that-was-removed", inside)],
            [world.Library],
            CancellationToken.None);

        Assert.Equal(0, result.MovedFileCount + result.MovedFolderCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("outside the library root"));
        Assert.Contains(result.Warnings, warning => warning.Contains("library root is no longer configured"));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(inside));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Moving_to_the_bin_a_folder_that_cannot_be_moved_leaves_it_in_place_removes_the_empty_holding_folder_and_warns()
    {
        await using var world = await StartWorldAsync();
        var file = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu.mkv"));
        using var block = DeleteBlock.Apply(file, posixFolder: world.Root);

        var result = await world.Bin.MoveAsync([new TrackedLibraryFile(world.Library.Id, file)], [world.Library], CancellationToken.None);
        block.Dispose();

        Assert.Equal(0, result.MovedFileCount + result.MovedFolderCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("could not move") && warning.Contains("recycle bin"));
        Assert.True(File.Exists(file), "the file is still in its folder");
        var holding = world.Root + ".deluno-recycle-bin";
        Assert.True(!Directory.Exists(holding) || Directory.GetFileSystemEntries(holding).Length == 0, "no empty item folder is left in the holding folder");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    // ---------------------------------------------------------------- moving a whole folder between libraries

    [Fact]
    public async Task Reassigning_a_film_to_another_library_when_the_records_cannot_follow_puts_the_folder_back_and_says_so()
    {
        await using var world = await StartWorldAsync();
        var otherRoot = Path.Combine(world.App.DataRoot, "movies-two");
        var other = await LibraryOnAsync(world, "Movies two", otherRoot);
        var file = Write(Path.Combine(world.Root, "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"));
        var created = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title = "Nosferatu", releaseYear = 1922, monitored = true, libraryId = world.Library.Id, searchForMissingNow = false });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        Assert.True((await world.Movies.GetByIdAsync(id, CancellationToken.None))!.HasFile);
        await world.SqlAsync("movies", "CREATE TRIGGER block_path_move BEFORE UPDATE OF file_path ON movie_wanted_state BEGIN SELECT RAISE(ABORT, 'database is locked'); END;");

        var beforeMove = await world.Movies.GetByIdAsync(id, CancellationToken.None);
        Assert.True(beforeMove!.FilePath == file, "tracked path was: " + beforeMove.FilePath + " library " + beforeMove.LibraryId + " vs " + world.Library.Id);
        var response = await world.App.Client.PostAsJsonAsync("/api/movies/bulk/reassign-library", new { movieIds = new[] { id }, fromLibraryId = world.Library.Id, toLibraryId = other.Id, moveFiles = true });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(File.Exists(file), "the folder was put back where it was: " + body);
        Assert.False(Directory.Exists(Path.Combine(otherRoot, "Nosferatu (1922)")));
        Assert.True(body.Contains("put back", StringComparison.Ordinal), "answer was: " + (int)response.StatusCode + " " + body);
        Assert.Equal(file, (await world.Movies.GetByIdAsync(id, CancellationToken.None))!.FilePath);
    }

    [Fact]
    public async Task When_the_folder_cannot_be_put_back_either_the_message_names_where_it_is_now_and_where_to_move_it_back_to()
    {
        var parent = Path.Combine(Path.GetTempPath(), "deluno-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(parent, "library-one", "Nosferatu (1922)");
            var destination = Path.Combine(parent, "library-two", "Nosferatu (1922)");
            Write(Path.Combine(source, "Nosferatu.mkv"));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
            // Something new now sits where the folder came from, so it cannot go back.
            Write(Path.Combine(source, "something-else.txt"));

            var putBack = FolderMoveRecovery.TryPutBack(source, destination);
            var message = FolderMoveRecovery.Describe("Nosferatu", source, destination, putBack, "database is locked", "run Rescan folder on Movies two");

            Assert.False(putBack);
            Assert.True(File.Exists(Path.Combine(destination, "Nosferatu.mkv")), "the film is safe at the new place");
            Assert.Contains($"the folder is now at {destination}", message);
            Assert.Contains($"Move it back to {source}", message);
            Assert.Contains("run Rescan folder on Movies two", message);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
