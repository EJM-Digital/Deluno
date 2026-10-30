using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Libraries.Data;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Library file listings, the recycle bin list and the existing-media issues list (#1329), read over HTTP from real library
/// folders and real SQLite files.
/// </summary>
public sealed class LibraryMaintenanceLowRowsTests
{
    [Fact]
    public async Task A_films_files_list_its_video_subtitle_and_other_files_unlabelled_with_one_library_and_an_unknown_film_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        await File.WriteAllTextAsync(Path.Combine(film.FolderPath, "Dune (2021) [Bluray-1080p].en.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        await File.WriteAllTextAsync(Path.Combine(film.FolderPath, "poster.jpg"), "poster");
        await File.WriteAllTextAsync(Path.Combine(film.FolderPath, "movie.nfo"), "<movie/>");

        var items = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}/files")).EnumerateArray().ToArray();

        string[] KindOf(string kind) => [.. items.Where(item => item.GetProperty("kind").GetString() == kind).Select(item => Path.GetFileName(item.GetProperty("path").GetString()!))];
        Assert.Equal([Path.GetFileName(film.FilePath)], KindOf("video"));
        Assert.Equal(["Dune (2021) [Bluray-1080p].en.srt"], KindOf("subtitle"));
        Assert.Equal(["movie.nfo", "poster.jpg"], KindOf("other").OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.True(items.Single(item => item.GetProperty("kind").GetString() == "video").GetProperty("isPrimary").GetBoolean());
        Assert.All(items, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("libraryName").ValueKind));
        Assert.All(items, item => Assert.Equal(film.LibraryId, item.GetProperty("libraryId").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/movies/no-such-film/files")).StatusCode);
    }

    [Fact]
    public async Task The_recycle_bin_lists_the_newest_removal_first()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var first = await ApiScenes.FilmOnDiskAsync(app, "First Out", 2001, libraryId);
        var second = await ApiScenes.FilmOnDiskAsync(app, "Second Out", 2002, libraryId);
        var third = await ApiScenes.FilmOnDiskAsync(app, "Third Out", 2003, libraryId);

        foreach (var film in new[] { first, third, second })
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/movies/{film.MovieId}/files") { Content = JsonContent.Create(new { path = film.FilePath }) };
            var response = await app.Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            await Task.Delay(30); // reason: the bin stamps each removal with the clock; a gap keeps two removals from sharing a stamp
        }

        var list = (await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin")).EnumerateArray().ToArray();

        Assert.Equal([second.FilePath, third.FilePath, first.FilePath], list.Select(item => item.GetProperty("originalPath").GetString()!).ToArray());
        Assert.All(list, item => Assert.True(File.Exists(item.GetProperty("recyclePath").GetString()!)));
    }

    [Fact]
    public async Task The_existing_media_issues_list_takes_a_hundred_by_default_and_holds_take_between_1_and_500()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var started = await app.Client.PostAsync($"/api/libraries/{libraryId}/import-existing", content: null);
        var runId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("id").GetString()!;
        var runs = app.Services.GetRequiredService<ILibraryImportRunsRepository>();
        for (var index = 0; index < 520; index++)
        {
            await runs.RecordIssueAsync(runId, libraryId, $"/films/Folder {index:000}", "unmatched", "No title in the folder name could be matched.", CancellationToken.None);
        }

        async Task<int> CountAsync(string query)
            => (await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/import-existing/issues{query}")).GetArrayLength();

        Assert.Equal(100, await CountAsync(string.Empty));
        Assert.Equal(1, await CountAsync("?take=0"));
        Assert.Equal(1, await CountAsync("?take=-5"));
        Assert.Equal(7, await CountAsync("?take=7"));
        Assert.Equal(500, await CountAsync("?take=99999"));
    }

    private sealed class TagRecorder : IOutboundNotificationService
    {
        public List<(string Category, string Title, IReadOnlyList<string>? Tags)> Sent { get; } = [];

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            Sent.Add((eventCategory, title, titleTags));
            return Task.CompletedTask;
        }

        public Task<NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static async Task SetFileDeletedAsync(ApplicationTestHost app, bool enabled)
    {
        var current = await app.Client.GetFromJsonAsync<NotificationPreferences>("/api/notification-preferences");
        current!.FileDeletedEnabled = enabled;
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync("/api/notification-preferences", current)).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleting_a_films_file_sends_file_deleted_with_the_films_tags_only_when_its_switch_is_on(bool switchedOn)
    {
        var outbound = new TagRecorder();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        await SetFileDeletedAsync(app, switchedOn);
        var film = await ApiScenes.FilmOnDiskAsync(app, "Tagged Film", 2020);
        await app.Services.GetRequiredService<IMediaTagStore>().ReplaceAsync(MediaKind.Movie, film.MovieId, [new MediaTagAssignment("kids", "kids")], CancellationToken.None);

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/movies/{film.MovieId}/files") { Content = JsonContent.Create(new { path = film.FilePath }) };
        var response = await app.Client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(film.FilePath));
        if (switchedOn)
        {
            var sent = Assert.Single(outbound.Sent, item => item.Category == "file.deleted");
            Assert.Equal(["kids"], sent.Tags);
        }
        else
        {
            Assert.DoesNotContain(outbound.Sent, item => item.Category == "file.deleted");
        }
    }

    [Fact]
    public async Task Deleting_an_episodes_file_writes_the_activity_line_pushes_a_change_and_sends_file_deleted_with_the_shows_tags()
    {
        var outbound = new TagRecorder();
        var realtime = new RecordingRealtimeEventPublisher();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.AddSingleton<IOutboundNotificationService>(outbound);
            services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(realtime));
        });
        await SetFileDeletedAsync(app, true);
        var show = await ApiScenes.ShowOnDiskAsync(app, "Tagged Show", 2020);
        await app.Services.GetRequiredService<IMediaTagStore>().ReplaceAsync(MediaKind.Series, show.SeriesId, [new MediaTagAssignment("kids", "kids")], CancellationToken.None);

        var response = await app.Client.DeleteAsync($"/api/series/episodes/{show.EpisodeId}/file");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(show.FilePath));
        var activity = await app.Client.GetFromJsonAsync<JsonElement>("/api/activity?take=50");
        var text = activity.GetRawText();
        Assert.Contains("library.episode.file-deleted", text, StringComparison.Ordinal);
        Assert.Contains("Tagged Show S01E01's file was moved to the recycle bin.", text, StringComparison.Ordinal);
        Assert.Contains(("Series", show.SeriesId), realtime.Published);
        var sent = Assert.Single(outbound.Sent, item => item.Category == "file.deleted");
        Assert.Equal(["kids"], sent.Tags);
    }

    [Fact]
    public async Task Creating_updating_and_deleting_a_library_each_push_a_Library_change_so_open_screens_refresh()
    {
        var realtime = new RecordingRealtimeEventPublisher();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(realtime)));

        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies", name: "Pushed Films");
        Assert.Contains(("Library", libraryId), realtime.Published);

        var held = (await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray().Single(library => library.GetProperty("id").GetString() == libraryId);
        var edit = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(held.GetRawText())!;
        var renamed = edit.ToDictionary(pair => pair.Key, pair => pair.Key == "name" ? JsonSerializer.SerializeToElement("Pushed Films Renamed") : pair.Value);
        var before = realtime.Published.Count(item => item == ("Library", libraryId));
        var updated = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}", renamed);
        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
        Assert.True(realtime.Published.Count(item => item == ("Library", libraryId)) > before, "the update pushed a Library change");

        before = realtime.Published.Count(item => item == ("Library", libraryId));
        var deleted = await app.Client.DeleteAsync($"/api/libraries/{libraryId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.True(realtime.Published.Count(item => item == ("Library", libraryId)) > before, "the delete pushed a Library change");
        Assert.DoesNotContain((await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray(), library => library.GetProperty("id").GetString() == libraryId);
    }

    [Fact]
    public async Task Removing_a_film_with_its_files_when_it_has_no_imported_files_says_so_and_moves_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var bystander = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Not Imported (2020)")).FullName, "Not Imported (2020).mkv");
        await File.WriteAllBytesAsync(bystander, [1, 2, 3]);
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Nothing Imported", releaseYear = 2019, monitored = true, libraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var movieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var removed = await app.Client.DeleteAsync($"/api/movies/{movieId}?deleteFiles=true");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Contains("Nothing Imported was removed from Deluno. It had no imported files to move.", await app.Client.GetStringAsync("/api/activity?take=50"));
        Assert.True(File.Exists(bystander), "a file Deluno never imported is not touched");
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin")).EnumerateArray());
    }

    [Fact]
    public async Task The_folder_check_proves_it_can_write_with_a_file_that_is_gone_again_and_never_touches_one_it_did_not_make()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var folder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "checked")).FullName;
        var decoy = Path.Combine(folder, ".deluno-write-test-decoy.tmp");
        await File.WriteAllTextAsync(decoy, "someone else's file");
        var before = Directory.GetFileSystemEntries(folder).Order().ToArray();

        for (var run = 0; run < 3; run++)
        {
            var response = await app.Client.PostAsJsonAsync("/api/filesystem/path-diagnostics", new { path = folder });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("writable").GetBoolean());
            Assert.True(body.GetProperty("readable").GetBoolean());
        }

        Assert.Equal(before, Directory.GetFileSystemEntries(folder).Order().ToArray());
        Assert.Equal("someone else's file", await File.ReadAllTextAsync(decoy));
    }
}
