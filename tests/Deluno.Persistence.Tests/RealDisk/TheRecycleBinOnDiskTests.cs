using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The recycle bin on a real temp folder: what it takes, where it keeps it, what it will not take, what a restore
/// does when the way back is blocked, and what an emptying really deletes from disk. The files are real, the bin
/// is the real service with its real state file, and the routes are the real ones.
/// </summary>
public sealed class TheRecycleBinOnDiskTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static string LibraryFile(RealDiskLibrary world, string name, byte fill = 0x11, int length = 4096)
    {
        var folder = Path.Combine(world.Root, Path.GetFileNameWithoutExtension(name));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, length).ToArray());
        return path;
    }

    private static string HoldingFolder(RealDiskLibrary world) => world.Root + ".deluno-recycle-bin";

    /// <summary>Moves an item's expiry into the past in the bin's own state file, the way time passing would.</summary>
    private static void Expire(RealDiskLibrary world, string itemId)
    {
        var statePath = Path.Combine(world.App.DataRoot, "recycle-bin", "state.json");
        var state = JsonNode.Parse(File.ReadAllText(statePath))!;
        foreach (var item in state["items"]!.AsArray())
        {
            if (string.Equals(item!["id"]!.GetValue<string>(), itemId, StringComparison.OrdinalIgnoreCase))
            {
                item["expiresUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
            }
        }

        File.WriteAllText(statePath, state.ToJsonString());
    }

    // ---------------------------------------------------------------- where it keeps things

    [Fact]
    public async Task A_file_goes_into_the_holding_folder_beside_the_library_root_by_a_rename_not_a_copy()
    {
        await using var world = await StartWorldAsync();
        var file = LibraryFile(world, "Sintel (2010).mkv");
        // A second name for the same bytes: a rename keeps it one file, a copy and delete would break the link.
        var otherName = Path.Combine(world.Downloads, "Sintel.mkv");
        RealHardLink.Create(file, otherName);

        var item = await world.Bin.StoreReplacementAsync(world.Library, file, file, CancellationToken.None);

        Assert.NotNull(item);
        Assert.StartsWith(HoldingFolder(world) + Path.DirectorySeparatorChar, item!.RecyclePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(item.RecyclePath));
        Assert.True(world.App.Services.GetRequiredService<IHardlinkInspector>().StillLinked(otherName, item.RecyclePath),
            "the bin took the file by a rename on the same volume, so the other name still shares its data");
    }

    [Fact]
    public async Task StoreRemovedFile_moves_only_the_named_file_and_never_the_folder_around_it()
    {
        await using var world = await StartWorldAsync();
        var file = LibraryFile(world, "Sintel (2010).mkv");
        var neighbour = Path.Combine(Path.GetDirectoryName(file)!, "Sintel (2010).en.srt");
        File.WriteAllText(neighbour, "subtitle");

        var item = await world.Bin.StoreRemovedFileAsync(world.Library, file, owner: null, CancellationToken.None);

        Assert.NotNull(item);
        Assert.False(item!.IsDirectory);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(neighbour), "the folder and the file beside it stay");
        Assert.True(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public async Task StoreRemovedFile_returns_null_and_moves_nothing_for_a_path_outside_the_library_root_or_one_that_climbs_out()
    {
        await using var world = await StartWorldAsync();
        var inside = LibraryFile(world, "Sintel (2010).mkv");
        var outsideFolder = Path.Combine(world.App.DataRoot, "elsewhere");
        Directory.CreateDirectory(outsideFolder);
        var outside = Path.Combine(outsideFolder, "Other.mkv");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var climbing = Path.Combine(Path.GetDirectoryName(inside)!, "..", "..", "elsewhere", "Other.mkv");

        Assert.Null(await world.Bin.StoreRemovedFileAsync(world.Library, outside, null, CancellationToken.None));
        Assert.Null(await world.Bin.StoreRemovedFileAsync(world.Library, climbing, null, CancellationToken.None));

        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(inside));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        Assert.False(Directory.Exists(HoldingFolder(world)) && Directory.GetFileSystemEntries(HoldingFolder(world)).Length > 0);
    }

    // ---------------------------------------------------------------- restore

    [Fact]
    public async Task Restore_onto_a_path_that_is_occupied_answers_409_and_overwrites_nothing()
    {
        await using var world = await StartWorldAsync();
        var file = LibraryFile(world, "Sintel (2010).mkv", fill: 0x11);
        var item = await world.Bin.StoreReplacementAsync(world.Library, file, file, CancellationToken.None);
        File.WriteAllBytes(file, Enumerable.Repeat((byte)0x99, 4096).ToArray());

        var response = await world.App.Client.PostAsync($"/api/recycle-bin/{item!.Id}/restore", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("The original library path is occupied", body);
        Assert.All(File.ReadAllBytes(file), value => Assert.Equal(0x99, value));
        Assert.All(File.ReadAllBytes(item.RecyclePath), value => Assert.Equal(0x11, value));
        Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Restore_that_cannot_put_the_file_back_answers_409_Restore_failed_and_keeps_the_item()
    {
        await using var world = await StartWorldAsync();
        var file = LibraryFile(world, "Sintel (2010).mkv", fill: 0x11);
        var item = await world.Bin.StoreReplacementAsync(world.Library, file, file, CancellationToken.None);
        // The folder it came from is now a plain file, so the way back cannot be made.
        var folder = Path.GetDirectoryName(file)!;
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        File.WriteAllText(folder, "in the way");

        var response = await world.App.Client.PostAsync($"/api/recycle-bin/{item!.Id}/restore", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Restore failed", body);
        Assert.True(File.Exists(item.RecyclePath), "the file is still safe in the bin");
        Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
    }

    // ---------------------------------------------------------------- what an emptying really deletes

    [Fact]
    public async Task The_cleanup_preview_names_what_an_empty_takes_and_the_cleanup_then_deletes_exactly_that_from_disk()
    {
        await using var world = await StartWorldAsync();
        var oldFile = LibraryFile(world, "Old (2001).mkv", length: 5000);
        var freshFile = LibraryFile(world, "Fresh (2002).mkv", length: 3000);
        var old = (await world.Bin.StoreReplacementAsync(world.Library, oldFile, oldFile, CancellationToken.None))!;
        var fresh = (await world.Bin.StoreReplacementAsync(world.Library, freshFile, freshFile, CancellationToken.None))!;
        Expire(world, old.Id);

        var preview = await world.App.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin/cleanup/preview");

        Assert.Equal(1, preview.GetProperty("expiredCount").GetInt32());
        Assert.Equal(0, preview.GetProperty("overCapacityCount").GetInt32());
        Assert.Equal(5000, preview.GetProperty("bytesFreed").GetInt64());
        Assert.Equal(old.Id, Assert.Single(preview.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        Assert.True(File.Exists(old.RecyclePath), "a preview deletes nothing");

        var emptied = await world.App.Client.PostAsync("/api/recycle-bin/cleanup", null);
        var removed = (await emptied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32();

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old.RecyclePath), "the expired file is really gone from disk");
        Assert.True(File.Exists(fresh.RecyclePath), "a file that has not expired stays");
        Assert.Equal(fresh.Id, Assert.Single(await world.Bin.ListAsync(CancellationToken.None)).Id);
    }

    [Fact]
    public async Task Saving_settings_with_a_smaller_size_limit_deletes_what_no_longer_fits_and_what_has_expired_at_once()
    {
        await using var world = await StartWorldAsync();
        var first = LibraryFile(world, "First (2001).mkv", length: 1_300_000);
        var second = LibraryFile(world, "Second (2002).mkv", length: 1_300_000);
        var third = LibraryFile(world, "Third (2003).mkv", length: 100);
        var firstItem = (await world.Bin.StoreReplacementAsync(world.Library, first, first, CancellationToken.None))!;
        await Wait.ClockMovesOnAsync();
        var secondItem = (await world.Bin.StoreReplacementAsync(world.Library, second, second, CancellationToken.None))!;
        await Wait.ClockMovesOnAsync();
        var thirdItem = (await world.Bin.StoreReplacementAsync(world.Library, third, third, CancellationToken.None))!;
        Expire(world, thirdItem.Id);

        var saved = await world.App.Client.PutAsJsonAsync("/api/recycle-bin/settings", new { retentionDays = 30, maxSizeMb = 2 });

        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.False(File.Exists(thirdItem.RecyclePath), "the expired item went at once");
        Assert.False(File.Exists(firstItem.RecyclePath), "the oldest item went because the bin no longer fits its limit");
        Assert.True(File.Exists(secondItem.RecyclePath), "the newest big item fits and stays");
        Assert.Equal(secondItem.Id, Assert.Single(await world.Bin.ListAsync(CancellationToken.None)).Id);
    }

    [Fact]
    public async Task A_retention_delete_that_fails_does_not_delete_another_item_to_make_up_the_space()
    {
        await using var world = await StartWorldAsync();
        var a = LibraryFile(world, "A (2001).mkv", length: 1_300_000);
        var b = LibraryFile(world, "B (2002).mkv", length: 1_300_000);
        var c = LibraryFile(world, "C (2003).mkv", length: 1_300_000);
        var itemA = (await world.Bin.StoreReplacementAsync(world.Library, a, a, CancellationToken.None))!;
        await Wait.ClockMovesOnAsync();
        var itemB = (await world.Bin.StoreReplacementAsync(world.Library, b, b, CancellationToken.None))!;
        await Wait.ClockMovesOnAsync();
        var itemC = (await world.Bin.StoreReplacementAsync(world.Library, c, c, CancellationToken.None))!;
        var settings = await world.Bin.GetSettingsAsync(CancellationToken.None);
        using var block = DeleteBlock.TryApply(itemA.RecyclePath);
        if (block is null)
        {
            return; // this account can delete anything (for example root in a container), so a delete cannot be made to fail
        }

        // Three 1.3 MB items against a 3 MB limit: only the oldest has to go. Its delete fails, and nothing else may go instead.
        await world.Bin.SaveSettingsAsync(settings with { MaxSizeMb = 3 }, CancellationToken.None);
        var removed = await world.Bin.CleanupAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(itemA.RecyclePath), "the locked file is still there");
        Assert.True(File.Exists(itemB.RecyclePath), "no other item was deleted to make up the space");
        Assert.True(File.Exists(itemC.RecyclePath));
        Assert.Equal(3, (await world.Bin.ListAsync(CancellationToken.None)).Count);
    }
}

/// <summary>Makes deleting one real file fail for the account running the test, and undoes it on dispose.</summary>
internal sealed class DeleteBlock : IDisposable
{
    private Action? _undo;

    private DeleteBlock(Action undo) => _undo = undo;

    /// <param name="file">The file to hold open on Windows.</param>
    /// <param name="posixFolder">The folder whose write permission is taken away elsewhere; the file's own folder when left out.</param>
    public static DeleteBlock? TryApply(string file, string? posixFolder = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            return new DeleteBlock(held.Dispose);
        }

        return ApplyOnPosix(posixFolder ?? Path.GetDirectoryName(file)!);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static DeleteBlock? ApplyOnPosix(string folder)
    {
        var original = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var probe = Path.Combine(folder, ".probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            File.SetUnixFileMode(folder, original);
            return null; // still writable: this account ignores the mode
        }
        catch (UnauthorizedAccessException)
        {
            return new DeleteBlock(() => File.SetUnixFileMode(folder, original));
        }
    }

    public void Dispose()
    {
        _undo?.Invoke();
        _undo = null;
    }
}
