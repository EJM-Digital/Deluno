using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The recycle bin routes against the real application, a real library folder and the real bin on disk. A file is put in the bin by
/// a second service pointed at the same data folder and running on an earlier clock, which is how an item that has sat there long
/// enough to expire looks to the running app.
/// </summary>
public sealed class RecycleBinOverHttpOnRealFilesTests
{
    [Fact]
    public async Task The_preview_names_what_an_empty_would_take_and_takes_nothing_and_the_empty_takes_exactly_that()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var libraryRoot = new Folder();
        var expired = Path.Combine(libraryRoot.Path, "Expired.Movie.2020.mkv");
        var recent = Path.Combine(libraryRoot.Path, "Recent.Movie.2026.mkv");
        await File.WriteAllTextAsync(expired, "expired");
        await File.WriteAllTextAsync(recent, "recent");
        (await app.Client.PutAsJsonAsync("/api/recycle-bin/settings", new { retentionDays = 7, maxSizeMb = 10000 })).EnsureSuccessStatusCode();
        // Today's item goes in first: putting anything in the bin also enforces retention, which would take the old one at once.
        var movedToday = await BinAsync(app, DateTimeOffset.UtcNow, libraryRoot.Path, recent);
        var movedLongAgo = await BinAsync(app, DateTimeOffset.UtcNow.AddDays(-40), libraryRoot.Path, expired);

        var preview = await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin/cleanup/preview");

        var named = preview.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToArray();
        Assert.Equal(movedLongAgo.Id, Assert.Single(named));
        Assert.Equal(1, preview.GetProperty("expiredCount").GetInt32());
        Assert.Equal(0, preview.GetProperty("overCapacityCount").GetInt32());
        Assert.True(File.Exists(movedLongAgo.RecyclePath), "a preview must take nothing");
        Assert.Equal(2, (await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin")).GetArrayLength());

        using var cleaned = await app.Client.PostAsync("/api/recycle-bin/cleanup", content: null);

        Assert.Equal(HttpStatusCode.OK, cleaned.StatusCode);
        Assert.Equal(1, (await cleaned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32());
        Assert.False(File.Exists(movedLongAgo.RecyclePath));
        Assert.True(File.Exists(movedToday.RecyclePath), "an item inside its retention is never taken");
        var left = await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin");
        Assert.Equal(movedToday.Id, Assert.Single(left.EnumerateArray()).GetProperty("id").GetString());

        using var again = await app.Client.PostAsync("/api/recycle-bin/cleanup", content: null);
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32());
        Assert.True(File.Exists(movedToday.RecyclePath));
    }

    [Fact]
    public async Task Restore_puts_the_file_back_where_it_was_and_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var libraryRoot = new Folder();
        var path = Path.Combine(libraryRoot.Path, "Example.Movie.2026.mkv");
        await File.WriteAllTextAsync(path, "the film");
        var item = await BinAsync(app, DateTimeOffset.UtcNow, libraryRoot.Path, path);
        Assert.False(File.Exists(path));

        using var response = await app.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Contains("Restored Example.Movie.2026.mkv", body.GetProperty("message").GetString());
        Assert.Equal("the film", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(item.RecyclePath));
        Assert.Equal(0, (await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin")).GetArrayLength());
    }

    [Fact]
    public async Task Restore_refuses_when_the_original_path_is_occupied_and_leaves_both_files_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var libraryRoot = new Folder();
        var path = Path.Combine(libraryRoot.Path, "Example.Movie.2026.mkv");
        await File.WriteAllTextAsync(path, "the old film");
        var item = await BinAsync(app, DateTimeOffset.UtcNow, libraryRoot.Path, path);
        await File.WriteAllTextAsync(path, "a newer file now sits here");

        using var response = await app.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("The original library path is occupied. Move or remove it before restoring this item.", body.GetProperty("message").GetString());
        Assert.Equal("a newer file now sits here", await File.ReadAllTextAsync(path));
        Assert.Equal("the old film", await File.ReadAllTextAsync(item.RecyclePath));
        Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin")).EnumerateArray());
    }

    [Fact]
    public async Task Restore_says_the_stored_file_is_gone_and_drops_that_stale_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var libraryRoot = new Folder();
        var path = Path.Combine(libraryRoot.Path, "Example.Movie.2026.mkv");
        await File.WriteAllTextAsync(path, "the film");
        var item = await BinAsync(app, DateTimeOffset.UtcNow, libraryRoot.Path, path);
        File.Delete(item.RecyclePath);

        using var response = await app.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("The stored recycle-bin item is no longer on disk.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.False(File.Exists(path));
        Assert.Equal(0, (await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin")).GetArrayLength());
    }

    [Fact]
    public async Task Deleting_permanently_removes_the_stored_file_and_the_second_delete_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var libraryRoot = new Folder();
        var path = Path.Combine(libraryRoot.Path, "Example.Movie.2026.mkv");
        var other = Path.Combine(libraryRoot.Path, "Other.Movie.2026.mkv");
        await File.WriteAllTextAsync(path, "the film");
        await File.WriteAllTextAsync(other, "left alone");
        var item = await BinAsync(app, DateTimeOffset.UtcNow, libraryRoot.Path, path);

        using var first = await app.Client.DeleteAsync($"/api/recycle-bin/{item.Id}");
        using var second = await app.Client.DeleteAsync($"/api/recycle-bin/{item.Id}");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(File.Exists(item.RecyclePath));
        Assert.False(File.Exists(path));
        Assert.Equal("left alone", await File.ReadAllTextAsync(other));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    private static async Task<RecycleBinItem> BinAsync(ApplicationTestHost app, DateTimeOffset at, string libraryRoot, string file)
    {
        var service = new RecycleBinService(
            Options.Create(new StoragePathOptions { DataRoot = app.DataRoot }),
            new FixedTimeProvider(at),
            NullLogger<RecycleBinService>.Instance);
        var now = DateTimeOffset.UtcNow;
        var library = new LibraryItem(
            "library-1", "Test library", "movies", "main", libraryRoot, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
        var moved = await service.MoveAsync([new TrackedLibraryFile("library-1", file)], [library], CancellationToken.None);
        return Assert.Single(moved.Items);
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deluno-recycle-http", Guid.NewGuid().ToString("N"));

        public Folder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
