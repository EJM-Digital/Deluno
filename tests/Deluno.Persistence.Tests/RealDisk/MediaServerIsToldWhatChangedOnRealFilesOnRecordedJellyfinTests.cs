using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// A media server told what changed in the library (#662), from real imports and removals: real files in a real library folder, the real import
/// pipeline, the real job queue, the real refresh handler and notifier, and a real SQLite backlog, with Jellyfin 12.2.0 answering over a real loopback
/// port from its own recorded replies (tests/fixtures/recorded/jellyfin). The server is asked to refresh by the same call it was recorded taking
/// (POST /Library/Media/Updated with the paths and their kind), so what the test reads is what Deluno really said to it.
/// </summary>
public sealed class MediaServerIsToldWhatChangedOnRealFilesOnRecordedJellyfinTests
{
    private const string Release = "Sintel.2010.WEB.1080p";

    private sealed class World : IAsyncDisposable
    {
        public required RealDiskLibrary Disk { get; init; }

        public required RecordedJellyfinServer Jellyfin { get; init; }

        public required string ServerId { get; init; }

        public ApplicationTestHost App => Disk.App;

        public async ValueTask DisposeAsync()
        {
            await Disk.DisposeAsync();
            await Jellyfin.DisposeAsync();
        }

        /// <summary>Runs every media server refresh job the import or removal queued, the way the job lane would.</summary>
        public async Task RunQueuedRefreshesAsync()
        {
            var jobs = await App.Services.GetRequiredService<IJobQueueRepository>().ListAsync(200, CancellationToken.None);
            var handler = App.Services.GetServices<IJobHandler>().Single(candidate => candidate.JobType == MediaServerJobTypes.Refresh);
            foreach (var job in jobs.Where(job => job.JobType == MediaServerJobTypes.Refresh && !_handled.Contains(job.Id)).OrderBy(job => job.CreatedUtc))
            {
                _handled.Add(job.Id);
                await handler.HandleAsync(job, CancellationToken.None);
            }
        }

        private readonly HashSet<string> _handled = [];

        public IReadOnlyList<(string Path, string Kind)> Told()
            => Jellyfin.UpdateBodies
                .SelectMany(body => JsonDocument.Parse(body).RootElement.GetProperty("Updates").EnumerateArray().Select(entry => (entry.GetProperty("Path").GetString()!, entry.GetProperty("UpdateType").GetString()!)))
                .ToArray();
    }

    private static async Task<World> StartAsync(string? serverUrl = null)
    {
        var jellyfin = await RecordedJellyfinServer.StartAsync();
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        var disk = await RealDiskLibrary.OpenAsync(app);
        var server = await app.Client.PostAsJsonAsync("/api/media-servers", new { kind = "jellyfin", name = "Living room", baseUrl = serverUrl ?? jellyfin.BaseUrl, token = "jf-secret-token-1", isEnabled = true });
        Assert.True(server.IsSuccessStatusCode, await server.Content.ReadAsStringAsync());
        return new World { Disk = disk, Jellyfin = jellyfin, ServerId = (await server.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()! };
    }

    [Fact]
    public async Task A_film_that_is_imported_is_told_to_the_server_as_Created_with_the_path_it_landed_at()
    {
        await using var world = await StartAsync();
        var movie = await world.Disk.AddMovieAsync("Sintel", 2010);
        var source = world.Disk.WriteDownload(Release + ".mkv");

        var result = await world.Disk.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: await world.Disk.RecordDispatchAsync(movie.Id, Release)));
        Assert.True(result.Succeeded, result.Message);
        Assert.Empty(world.Jellyfin.UpdateBodies);
        await world.RunQueuedRefreshesAsync();

        Assert.Equal([(result.Response!.Preview.DestinationPath, "Created")], world.Told());
        Assert.True(File.Exists(result.Response.Preview.DestinationPath));
        Assert.DoesNotContain((await world.App.Client.GetFromJsonAsync<JsonElement>("/api/media-servers")).EnumerateArray().Single().GetProperty("healthStatus").GetString(), new[] { "unreachable", "degraded" });
        var name = (await world.App.Client.GetFromJsonAsync<JsonElement>("/api/media-servers")).EnumerateArray().Single().GetProperty("name").GetString();
        Assert.Contains($"Told {name} about 1 new or changed file.", await world.App.Client.GetStringAsync("/api/activity?take=100"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_upgrade_that_replaces_a_file_tells_the_server_the_new_file_is_Created_and_the_one_it_replaced_is_Deleted()
    {
        await using var world = await StartAsync();
        var (movie, existing) = await world.Disk.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        await world.RunQueuedRefreshesAsync();
        var firstTold = world.Told().Count;
        var incoming = world.Disk.WriteDownload("Sintel.2010.Bluray.1080p.mkv", fill: 0x22);

        var result = await world.Disk.ImportAsync(RealDiskLibrary.Request(incoming, "Sintel", 2010, overwrite: true, expectedExistingPath: existing, dispatchId: await world.Disk.RecordDispatchAsync(movie.Id, "Sintel.2010.Bluray.1080p")));
        Assert.True(result.Succeeded, result.Message);
        await world.RunQueuedRefreshesAsync();

        var told = world.Told().Skip(firstTold).ToArray();
        Assert.NotEqual(existing, result.Response!.Preview.DestinationPath);
        Assert.Equal([(result.Response.Preview.DestinationPath, "Created"), (existing, "Deleted")], told.OrderBy(change => change.Kind, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_film_removed_with_its_files_tells_the_server_each_tracked_file_is_Deleted_and_one_removed_without_deleting_files_tells_it_nothing()
    {
        await using var world = await StartAsync();
        var first = await ApiScenes.FilmOnDiskAsync(world.App, "Dune", 2021, world.Disk.Library.Id);
        var second = await ApiScenes.FilmOnDiskAsync(world.App, "Arrival", 2016, world.Disk.Library.Id);

        var kept = await world.App.Client.DeleteAsync($"/api/movies/{second.MovieId}");
        await world.RunQueuedRefreshesAsync();
        Assert.True(kept.IsSuccessStatusCode, await kept.Content.ReadAsStringAsync());
        Assert.Empty(world.Jellyfin.UpdateBodies);
        Assert.True(File.Exists(second.FilePath), "without deleteFiles the file stays");

        var removed = await world.App.Client.DeleteAsync($"/api/movies/{first.MovieId}?deleteFiles=true");
        await world.RunQueuedRefreshesAsync();

        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        Assert.Equal([(first.FilePath, "Deleted")], world.Told());
        Assert.False(File.Exists(first.FilePath));
    }

    [Fact]
    public async Task A_server_that_does_not_answer_is_owed_what_it_missed_in_the_database_and_is_told_it_all_in_one_call_when_it_answers_again()
    {
        await using var world = await StartAsync(serverUrl: "http://127.0.0.1:1");
        var first = await ApiScenes.FilmOnDiskAsync(world.App, "Dune", 2021, world.Disk.Library.Id);
        await world.App.Client.DeleteAsync($"/api/movies/{first.MovieId}?deleteFiles=true");
        await world.RunQueuedRefreshesAsync();

        var backlog = world.App.Services.GetRequiredService<IMediaServerRefreshBacklogRepository>();
        var owed = await backlog.FindAsync(world.ServerId, "movies", CancellationToken.None);
        Assert.NotNull(owed);
        Assert.Equal([(first.FilePath, MediaServerChangeKind.Deleted)], owed!.Changes.Select(change => (change.Path, change.Kind)));
        Assert.Equal(1, owed.AttemptCount);
        Assert.Empty(world.Jellyfin.UpdateBodies);

        var back = await world.App.Client.PutAsJsonAsync($"/api/media-servers/{world.ServerId}", new { kind = "jellyfin", name = "Living room", baseUrl = world.Jellyfin.BaseUrl, token = "", isEnabled = true });
        Assert.True(back.IsSuccessStatusCode, await back.Content.ReadAsStringAsync());
        var second = await ApiScenes.FilmOnDiskAsync(world.App, "Arrival", 2016, world.Disk.Library.Id);
        await world.App.Client.DeleteAsync($"/api/movies/{second.MovieId}?deleteFiles=true");
        await world.RunQueuedRefreshesAsync();

        var told = world.Told();
        Assert.Equal(2, told.Count);
        Assert.Single(world.Jellyfin.UpdateBodies);
        Assert.Contains((first.FilePath, "Deleted"), told);
        Assert.Contains((second.FilePath, "Deleted"), told);
        Assert.Null(await backlog.FindAsync(world.ServerId, "movies", CancellationToken.None));
        Assert.Contains("answered again", await world.App.Client.GetStringAsync("/api/activity?take=100"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_a_server_is_owed_survives_a_restart_and_is_found_listed_and_cleared_in_the_database_file()
    {
        await using var world = await StartAsync();
        var otherServer = await world.App.Client.PostAsJsonAsync("/api/media-servers", new { kind = "jellyfin", name = "Bedroom", baseUrl = world.Jellyfin.BaseUrl, token = "jf-secret-token-2", isEnabled = true });
        var otherId = (await otherServer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-19T09:00:00Z"));
        var factory = world.App.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
        var firstRun = new SqliteMediaServerRefreshBacklogRepository(factory, clock);
        var path = "/movies/Sintel (2010)/Sintel (2010).mkv";

        var failed = await firstRun.RecordFailureAsync(world.ServerId, "movies", [new MediaServerPathChange(path, MediaServerChangeKind.Created)], CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(7));
        var failedAgain = await firstRun.RecordFailureAsync(world.ServerId, "movies", [new MediaServerPathChange(path, MediaServerChangeKind.Created), new MediaServerPathChange(path + ".nfo", MediaServerChangeKind.Created)], CancellationToken.None);
        await firstRun.RecordFailureAsync(otherId, "tv", [new MediaServerPathChange("/tv/Show/S01E01.mkv", MediaServerChangeKind.Deleted)], CancellationToken.None);

        var afterRestart = new SqliteMediaServerRefreshBacklogRepository(factory, clock);
        var found = await afterRestart.FindAsync(world.ServerId, "movies", CancellationToken.None);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal(2, failedAgain.AttemptCount);
        Assert.Equal(failed.FirstFailedUtc, failedAgain.FirstFailedUtc);
        Assert.True(failedAgain.NextAttemptUtc > failed.NextAttemptUtc);
        Assert.Equal(2, found!.AttemptCount);
        Assert.Equal(failed.FirstFailedUtc, found.FirstFailedUtc);
        Assert.Equal([path, path + ".nfo"], found.Changes.Select(change => change.Path));
        Assert.Equal(2, (await afterRestart.ListAsync(CancellationToken.None)).Count);

        await afterRestart.ClearAsync(world.ServerId, "movies", CancellationToken.None);

        Assert.Null(await afterRestart.FindAsync(world.ServerId, "movies", CancellationToken.None));
        Assert.NotNull(await afterRestart.FindAsync(otherId, "tv", CancellationToken.None));
        Assert.Single(await afterRestart.ListAsync(CancellationToken.None));
    }

    private static async Task MapAsync(World world, string delunoPath, string serverPath, int priority = 100, bool enabled = true, string? serverId = null)
    {
        var made = await world.App.Client.PostAsJsonAsync($"/api/media-servers/{serverId ?? world.ServerId}/path-mappings", new { delunoPath, serverPath, isEnabled = enabled, priority });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
    }

    private static async Task TellAsync(World world, params MediaServerPathChange[] changes)
        => await world.App.Services.GetRequiredService<Deluno.Integrations.MediaServers.IMediaServerRefreshNotifier>().NotifyAsync("movies", changes, CancellationToken.None);

    [Fact]
    public async Task A_path_is_sent_to_the_server_as_the_server_sees_it_the_longest_matching_root_first_then_the_lowest_priority_and_never_a_disabled_mapping()
    {
        await using var world = await StartAsync();
        await MapAsync(world, "/data/movies", "/media/Movies", priority: 10);
        await MapAsync(world, "/data/movies/Kids", "/media/Kids", priority: 10);
        await MapAsync(world, "/data/movies/Shared", "/media/Late", priority: 50);
        await MapAsync(world, "/data/movies/shared/", "/media/Early", priority: 5);
        await MapAsync(world, "/data/movies/Off", "/media/Off", priority: 1, enabled: false);

        await TellAsync(
            world,
            new MediaServerPathChange("/data/movies/Dune (2021)/Dune (2021).mkv", MediaServerChangeKind.Created),
            new MediaServerPathChange("/data/movies/Kids/Up (2009)/Up (2009).mkv", MediaServerChangeKind.Created),
            new MediaServerPathChange("/data/movies/Shared/Heat (1995)/Heat (1995).mkv", MediaServerChangeKind.Modified),
            new MediaServerPathChange("/data/movies/Off/Alien (1979)/Alien (1979).mkv", MediaServerChangeKind.Deleted),
            new MediaServerPathChange("/elsewhere/Film (2000)/Film (2000).mkv", MediaServerChangeKind.Created));

        Assert.Single(world.Jellyfin.UpdateBodies);
        Assert.Equal(
            [
                ("/media/Movies/Dune (2021)/Dune (2021).mkv", "Created"),
                ("/media/Kids/Up (2009)/Up (2009).mkv", "Created"),
                ("/media/Early/Heat (1995)/Heat (1995).mkv", "Modified"),
                ("/media/Movies/Off/Alien (1979)/Alien (1979).mkv", "Deleted"),
                ("/elsewhere/Film (2000)/Film (2000).mkv", "Created")
            ],
            world.Told());
    }

    [Fact]
    public async Task A_Windows_shaped_server_root_keeps_its_backslashes_and_a_path_equal_to_the_root_maps_to_the_root_whatever_the_case_or_trailing_separator()
    {
        await using var world = await StartAsync();
        await MapAsync(world, "C:\\Media\\Movies\\", "D:\\Library\\Films\\");
        await MapAsync(world, "/mnt/nas/tv", "/volume1/tv/");

        await TellAsync(
            world,
            new MediaServerPathChange("c:/media/movies/Film (2020)/Film (2020).mkv", MediaServerChangeKind.Created),
            new MediaServerPathChange("C:\\MEDIA\\MOVIES", MediaServerChangeKind.Modified),
            new MediaServerPathChange("/mnt/nas/tv/Show/Season 01/S01E01.mkv", MediaServerChangeKind.Created),
            new MediaServerPathChange("/mnt/nas/tv/", MediaServerChangeKind.Modified),
            new MediaServerPathChange("/mnt/nas/tvshows/Other/S01E01.mkv", MediaServerChangeKind.Created));

        Assert.Equal(
            [
                ("D:\\Library\\Films\\Film (2020)\\Film (2020).mkv", "Created"),
                ("D:\\Library\\Films", "Modified"),
                ("/volume1/tv/Show/Season 01/S01E01.mkv", "Created"),
                ("/volume1/tv", "Modified"),
                ("/mnt/nas/tvshows/Other/S01E01.mkv", "Created")
            ],
            world.Told());
    }

    [Fact]
    public async Task A_switched_off_server_is_told_nothing_and_a_removed_one_has_its_backlog_dropped_when_its_retry_comes()
    {
        await using var world = await StartAsync(serverUrl: "http://127.0.0.1:1");
        await TellAsync(world, new MediaServerPathChange("/data/movies/Dune (2021)/Dune (2021).mkv", MediaServerChangeKind.Created));
        var backlog = world.App.Services.GetRequiredService<IMediaServerRefreshBacklogRepository>();
        Assert.NotNull(await backlog.FindAsync(world.ServerId, "movies", CancellationToken.None));
        var notifier = world.App.Services.GetRequiredService<Deluno.Integrations.MediaServers.IMediaServerRefreshNotifier>();

        var off = await world.App.Client.PutAsJsonAsync($"/api/media-servers/{world.ServerId}", new { kind = "jellyfin", name = "Living room", baseUrl = world.Jellyfin.BaseUrl, token = "", isEnabled = false });
        Assert.True(off.IsSuccessStatusCode, await off.Content.ReadAsStringAsync());
        await TellAsync(world, new MediaServerPathChange("/data/movies/Arrival (2016)/Arrival (2016).mkv", MediaServerChangeKind.Created));
        Assert.Empty(world.Jellyfin.UpdateBodies);

        Assert.True(await notifier.RetryAsync(world.ServerId, "movies", CancellationToken.None));
        Assert.Null(await backlog.FindAsync(world.ServerId, "movies", CancellationToken.None));
        Assert.Empty(world.Jellyfin.UpdateBodies);

        var gone = await world.App.Client.PostAsJsonAsync("/api/media-servers", new { kind = "jellyfin", name = "Bedroom", baseUrl = "http://127.0.0.1:1", token = "jf-secret-token-2", isEnabled = true });
        var goneId = (await gone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await backlog.RecordFailureAsync(goneId, "movies", [new MediaServerPathChange("/data/movies/Heat (1995)/Heat (1995).mkv", MediaServerChangeKind.Created)], CancellationToken.None);
        await world.App.Client.DeleteAsync($"/api/media-servers/{goneId}");

        Assert.True(await notifier.RetryAsync(goneId, "movies", CancellationToken.None));
        Assert.Null(await backlog.FindAsync(goneId, "movies", CancellationToken.None));
    }

    [Fact]
    public async Task A_change_made_while_the_server_is_down_goes_out_with_the_owed_ones_in_one_call_and_a_later_change_to_the_same_path_replaces_the_earlier_one()
    {
        await using var world = await StartAsync(serverUrl: "http://127.0.0.1:1");
        var dune = "/data/movies/Dune (2021)/Dune (2021).mkv";
        await TellAsync(world, new MediaServerPathChange(dune, MediaServerChangeKind.Created));
        await TellAsync(world, new MediaServerPathChange("/data/movies/Arrival (2016)/Arrival (2016).mkv", MediaServerChangeKind.Created));
        var backlog = world.App.Services.GetRequiredService<IMediaServerRefreshBacklogRepository>();
        Assert.Equal(2, (await backlog.FindAsync(world.ServerId, "movies", CancellationToken.None))!.Changes.Count);
        Assert.Empty(world.Jellyfin.UpdateBodies);

        var back = await world.App.Client.PutAsJsonAsync($"/api/media-servers/{world.ServerId}", new { kind = "jellyfin", name = "Living room", baseUrl = world.Jellyfin.BaseUrl, token = "", isEnabled = true });
        Assert.True(back.IsSuccessStatusCode, await back.Content.ReadAsStringAsync());
        await TellAsync(world, new MediaServerPathChange(dune, MediaServerChangeKind.Deleted));

        Assert.Single(world.Jellyfin.UpdateBodies);
        Assert.Equal(
            [("/data/movies/Arrival (2016)/Arrival (2016).mkv", "Created"), (dune, "Deleted")],
            world.Told().OrderBy(change => change.Kind, StringComparer.Ordinal).ToArray());
        Assert.Null(await backlog.FindAsync(world.ServerId, "movies", CancellationToken.None));
    }

    [Fact]
    public async Task A_second_mapping_for_a_folder_already_mapped_is_refused_in_plain_words_not_with_a_server_error()
    {
        await using var world = await StartAsync();
        await MapAsync(world, "/data/movies", "/media/Movies");

        var again = await world.App.Client.PostAsJsonAsync($"/api/media-servers/{world.ServerId}/path-mappings", new { delunoPath = "/data/movies", serverPath = "/media/Other", isEnabled = true, priority = 1 });

        Assert.InRange((int)again.StatusCode, 400, 499);
    }

    [Fact]
    public async Task A_movie_file_deleted_from_the_Files_tab_tells_the_server_the_file_is_Deleted()
    {
        await using var world = await StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(world.App, "Dune", 2021, world.Disk.Library.Id);

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/movies/{film.MovieId}/files") { Content = JsonContent.Create(new { path = film.FilePath }) };
        var removed = await world.App.Client.SendAsync(request);
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        await world.RunQueuedRefreshesAsync();

        Assert.False(File.Exists(film.FilePath));
        Assert.Equal([(film.FilePath, "Deleted")], world.Told());
    }

    [Fact]
    public async Task A_series_removed_with_its_files_tells_the_server_every_episode_file_it_held_is_Deleted()
    {
        await using var world = await StartAsync();
        var tvLibrary = await ApiScenes.CreateLibraryAsync(world.App, "tv");
        var root = await ApiScenes.LibraryRootAsync(world.App, tvLibrary);
        var episodes = new[] { 1, 2 }.Select(number => Path.Combine(root, "The Wire", "Season 01", $"The Wire S01E{number:D2}.mkv")).ToArray();
        foreach (var episode in episodes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(episode)!);
            await File.WriteAllBytesAsync(episode, [1, 2, 3, 4]);
        }

        var series = world.App.Services.GetRequiredService<Deluno.Series.Data.ISeriesCatalogRepository>();
        await series.ImportExistingAsync(
            tvLibrary, "The Wire", 2002, "covered", "Imported.", "Bluray-1080p", "Bluray-1080p", true, null, null,
            [new Deluno.Series.Contracts.ImportedEpisodeItem(1, 1, HasFile: true, FilePath: episodes[0], FileSizeBytes: 4), new Deluno.Series.Contracts.ImportedEpisodeItem(1, 2, HasFile: true, FilePath: episodes[1], FileSizeBytes: 4)],
            CancellationToken.None);
        var seriesId = (await series.ListAsync(CancellationToken.None)).Single(item => item.Title == "The Wire").Id;

        var removed = await world.App.Client.DeleteAsync($"/api/series/{seriesId}?deleteFiles=true");
        await world.RunQueuedRefreshesAsync();

        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        Assert.All(episodes, episode => Assert.False(File.Exists(episode)));
        Assert.Equal(episodes.Select(episode => (episode, "Deleted")).OrderBy(change => change.episode, StringComparer.Ordinal), world.Told().OrderBy(change => change.Path, StringComparer.Ordinal));
    }

    // ---- #1365: every other way a file changes place tells the server too ----

    [Fact]
    public async Task An_episode_file_deleted_from_the_Files_tab_tells_the_server_the_file_is_Deleted()
    {
        await using var world = await StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(world.App);

        var removed = await world.App.Client.DeleteAsync($"/api/series/episodes/{show.EpisodeId}/file");
        await world.RunQueuedRefreshesAsync();

        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        Assert.Equal([(show.FilePath, "Deleted")], world.Told());
    }

    [Fact]
    public async Task A_film_restored_from_the_recycle_bin_tells_the_server_the_file_is_back_as_Created()
    {
        await using var world = await StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(world.App, "Dune", 2021, world.Disk.Library.Id);
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/movies/{film.MovieId}/files") { Content = JsonContent.Create(new { path = film.FilePath }) };
        Assert.True((await world.App.Client.SendAsync(request)).IsSuccessStatusCode);
        var bin = await world.App.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin");
        var itemId = bin.EnumerateArray().Single().GetProperty("id").GetString();

        var restored = await world.App.Client.PostAsync($"/api/recycle-bin/{itemId}/restore", null);
        await world.RunQueuedRefreshesAsync();

        Assert.True(restored.IsSuccessStatusCode, await restored.Content.ReadAsStringAsync());
        Assert.True(File.Exists(film.FilePath));
        Assert.Equal([(film.FilePath, "Created"), (film.FilePath, "Deleted")], world.Told().OrderBy(change => change.Kind, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_film_folder_renamed_tells_the_server_the_old_file_is_Deleted_and_the_new_one_Created()
    {
        await using var world = await StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(world.App, "Dune", 2021, world.Disk.Library.Id);
        await ApiScenes.ExecuteAsync(world.App, "movies", $"UPDATE movie_entries SET title = 'Dune Part Two' WHERE id = '{film.MovieId}'");

        var renamed = await world.App.Client.PostAsJsonAsync("/api/movies/bulk/rename-apply", new { movieIds = new[] { film.MovieId } });
        await world.RunQueuedRefreshesAsync();

        Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync());
        Assert.False(File.Exists(film.FilePath));
        var told = world.Told();
        Assert.Contains((film.FilePath, "Deleted"), told);
        var created = Assert.Single(told, change => change.Kind == "Created");
        Assert.True(File.Exists(created.Path), created.Path);
    }

    [Fact]
    public async Task A_film_moved_to_another_library_tells_the_server_the_old_file_is_Deleted_and_the_new_one_Created()
    {
        await using var world = await StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(world.App, "Dune", 2021, world.Disk.Library.Id);
        var other = await ApiScenes.CreateLibraryAsync(world.App, "movies");

        var moved = await world.App.Client.PostAsJsonAsync("/api/movies/bulk/reassign-library", new { movieIds = new[] { film.MovieId }, fromLibraryId = film.LibraryId, toLibraryId = other, moveFiles = true });
        await world.RunQueuedRefreshesAsync();

        Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
        var told = world.Told();
        Assert.Contains((film.FilePath, "Deleted"), told);
        var created = Assert.Single(told, change => change.Kind == "Created");
        Assert.True(File.Exists(created.Path), created.Path);
    }

    [Fact]
    public async Task A_show_folder_renamed_or_moved_tells_the_server_each_episode_file_old_and_new()
    {
        await using var world = await StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(world.App, "The Wire", 2002);
        await ApiScenes.ExecuteAsync(world.App, "series", $"UPDATE series_entries SET title = 'The Wire Again' WHERE id = '{show.SeriesId}'");

        var renamed = await world.App.Client.PostAsJsonAsync("/api/series/bulk/rename-apply", new { seriesIds = new[] { show.SeriesId } });
        await world.RunQueuedRefreshesAsync();

        Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync());
        var told = world.Told();
        Assert.Contains((show.FilePath, "Deleted"), told);
        var created = Assert.Single(told, change => change.Kind == "Created");
        Assert.True(File.Exists(created.Path), created.Path);
    }
}
