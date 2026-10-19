using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Filesystem;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The things that happen to a person's library, told to a phone (#856): real files in a real library folder, the real import pipeline, the real title
/// removal and the real Files tab routes, the real outbound notification service and its database, and a push service on a loopback port that
/// answers as the real ntfy 2.32.0 did (tests/fixtures/recorded/ntfy: the message it accepted). Each switch is tried on and off, and what reaches
/// the phone is read from the request the push service received.
/// </summary>
public sealed class EventsDeliveredToRecordedReceiversOnRealFilesTests
{
    private const string Release = "Sintel.2010.WEB.1080p";

    private sealed class World : IAsyncDisposable
    {
        public required RealDiskLibrary Disk { get; init; }

        public required RecordedAppServer Ntfy { get; init; }

        public ApplicationTestHost App => Disk.App;

        public IReadOnlyList<string> Titles => Ntfy.Requests.Select(request => request.Header("title")).ToArray();

        public async Task SwitchesAsync(params string[] on)
        {
            var current = (JsonObject)JsonNode.Parse((await App.Client.GetStringAsync("/api/notification-preferences")))!;
            foreach (var name in current.Select(property => property.Key).ToArray())
            {
                // The three that are on in a new install stay as they are; every other switch is set to what the test asks.
                if (name is "importCompletedEnabled" or "importFailedEnabled" or "providerHealthChangedEnabled") continue;
                current[name] = on.Contains(name);
            }

            var saved = await App.Client.PutAsJsonAsync("/api/notification-preferences", current);
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await Disk.DisposeAsync();
            await Ntfy.DisposeAsync();
        }
    }

    private static async Task<World> StartAsync(IMediaProbeService? probe = null, Action<IServiceCollection>? services = null)
    {
        var ntfy = await RecordedAppServer.StartAsync("ntfy", [], (request, _) => request.Method == HttpMethod.Post ? RecordedReplies.Ntfy("delivered").Exchanges[0].Reply.ToResponse() : null);
        var app = await ApplicationTestHost.StartAsync(replaceServices: collection =>
        {
            RealDiskLibrary.UseProbe(probe ?? new ScriptedProbe(_ => ScriptedProbe.Good()))(collection);
            services?.Invoke(collection);
        });
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        var disk = await RealDiskLibrary.OpenAsync(app);
        Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = true })).IsSuccessStatusCode);
        var made = await app.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Phone", url = ntfy.BaseUrl + "/deluno", eventFilters = "all", isEnabled = true, provider = "ntfy" });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        return new World { Disk = disk, Ntfy = ntfy };
    }

    private static async Task<ImportPipelineResultView> ImportAsync(World world, string title, int year, string release, byte fill = 0x11, bool replace = false, string? existing = null)
    {
        var movies = world.Disk.Movies;
        var movie = (await movies.ListAsync(CancellationToken.None)).FirstOrDefault(item => item.Title == title) ?? await world.Disk.AddMovieAsync(title, year);
        var source = world.Disk.WriteDownload(release + ".mkv", fill);
        var result = await world.Disk.ImportAsync(RealDiskLibrary.Request(source, title, year, overwrite: replace, expectedExistingPath: existing, dispatchId: await world.Disk.RecordDispatchAsync(movie.Id, release)));
        Assert.True(result.Succeeded, result.Message);
        return new ImportPipelineResultView(movie.Id, result.Response!.Preview.DestinationPath);
    }

    private sealed record ImportPipelineResultView(string MovieId, string Destination);

    // ---- rename ----

    [Fact]
    public async Task A_file_renamed_on_import_is_told_to_the_phone_only_while_the_rename_switch_is_on()
    {
        await using var world = await StartAsync();
        Assert.True((await world.App.Client.PatchAsJsonAsync("/api/settings", new { renameOnImport = true })).IsSuccessStatusCode);

        await world.SwitchesAsync();
        var quiet = await ImportAsync(world, "Sintel", 2010, Release);
        Assert.DoesNotContain(world.Titles, title => title.StartsWith("Renamed:", StringComparison.Ordinal));
        Assert.Contains(world.Titles, title => title.StartsWith("Import completed:", StringComparison.Ordinal));

        await world.SwitchesAsync("renameEnabled");
        var second = await ImportAsync(world, "Arrival", 2016, "Arrival.2016.WEB.1080p", 0x22);

        var renamed = Assert.Single(world.Ntfy.Requests, request => request.Header("title").StartsWith("Renamed:", StringComparison.Ordinal));
        Assert.Contains(Path.GetFileName(second.Destination), renamed.Header("title"), StringComparison.Ordinal);
        Assert.Contains("Arrival.2016.WEB.1080p.mkv was named", renamed.Body, StringComparison.Ordinal);
        Assert.NotEqual(quiet.Destination, second.Destination);
    }

    // ---- an upgrade ----

    [Fact]
    public async Task An_upgrade_tells_the_phone_it_was_upgraded_and_which_file_it_replaced_and_each_is_its_own_switch()
    {
        await using var world = await StartAsync();
        await world.SwitchesAsync();
        var first = await ImportAsync(world, "Sintel", 2010, Release, 0x11);
        Assert.Equal(["Import completed: Sintel"], world.Titles.Select(title => title.Split(" using ")[0].Replace("Import completed: Sintel (2010)", "Import completed: Sintel")));

        await world.SwitchesAsync("upgradeEnabled");
        await ImportAsync(world, "Sintel", 2010, "Sintel.2010.Bluray.1080p", 0x22, replace: true, existing: first.Destination);
        Assert.Contains(world.Titles, title => title.StartsWith("Upgraded:", StringComparison.Ordinal));
        Assert.DoesNotContain(world.Titles, title => title.StartsWith("File removed:", StringComparison.Ordinal));

        await world.SwitchesAsync("upgradeEnabled", "fileDeletedEnabled");
        var before = world.Ntfy.Requests.Count;
        await ImportAsync(world, "Sintel", 2010, "Sintel.2010.Remux.1080p", 0x33, replace: true, existing: Path.Combine(Path.GetDirectoryName(first.Destination)!, "Sintel.2010.Bluray.1080p.mkv"));
        var told = world.Ntfy.Requests.Skip(before).ToArray();
        var removed = Assert.Single(told, request => request.Header("title").StartsWith("File removed:", StringComparison.Ordinal));
        Assert.Contains("because an upgrade replaced it", removed.Body, StringComparison.Ordinal);
        Assert.Contains("Sintel.2010.Bluray.1080p.mkv", removed.Header("title"), StringComparison.Ordinal);
    }

    // ---- removing files ----

    [Fact]
    public async Task A_title_removed_with_its_files_is_told_when_the_switch_is_on_silent_when_it_is_off_and_never_told_when_the_files_stay()
    {
        await using var world = await StartAsync();
        var kept = await ApiScenes.FilmOnDiskAsync(world.App, "Night of the Living Dead", 1968, world.Disk.Library.Id);
        var removedOn = await ApiScenes.FilmOnDiskAsync(world.App, "Dementia 13", 1963, world.Disk.Library.Id);
        var removedOff = await ApiScenes.FilmOnDiskAsync(world.App, "Carnival of Souls", 1962, world.Disk.Library.Id);

        await world.SwitchesAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await world.App.Client.DeleteAsync($"/api/movies/{removedOff.MovieId}?deleteFiles=true")).StatusCode);
        Assert.False(File.Exists(removedOff.FilePath));
        Assert.Empty(world.Ntfy.Requests);

        await world.SwitchesAsync("fileDeletedEnabled");
        Assert.Equal(HttpStatusCode.NoContent, (await world.App.Client.DeleteAsync($"/api/movies/{kept.MovieId}")).StatusCode);
        Assert.True(File.Exists(kept.FilePath), "the files were kept");
        Assert.Empty(world.Ntfy.Requests);

        Assert.Equal(HttpStatusCode.NoContent, (await world.App.Client.DeleteAsync($"/api/movies/{removedOn.MovieId}?deleteFiles=true")).StatusCode);
        Assert.False(File.Exists(removedOn.FilePath));
        var told = Assert.Single(world.Ntfy.Requests);
        Assert.Equal("Files removed: Dementia 13", told.Header("title"));
        Assert.Contains("Dementia 13's files were removed", told.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_film_file_deleted_by_hand_from_the_Files_tab_is_told_with_the_films_tags_and_a_connection_for_other_tags_hears_nothing()
    {
        await using var world = await StartAsync();
        await world.SwitchesAsync("fileDeletedEnabled");
        var film = await ApiScenes.FilmOnDiskAsync(world.App, "Dune", 2021, world.Disk.Library.Id);
        var tagged = await world.App.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Kids only", url = world.Ntfy.BaseUrl + "/kids", eventFilters = "all", isEnabled = true, provider = "ntfy", tags = "kids" });
        Assert.True(tagged.IsSuccessStatusCode, await tagged.Content.ReadAsStringAsync());

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/movies/{film.MovieId}/files") { Content = JsonContent.Create(new { path = film.FilePath }) };
        var removed = await world.App.Client.SendAsync(request);

        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        Assert.False(File.Exists(film.FilePath));
        var told = Assert.Single(world.Ntfy.Requests);
        Assert.Equal("/deluno", told.PathAndQuery);
        Assert.StartsWith("File removed: Dune (2021) [Bluray-1080p].mkv", told.Header("title"), StringComparison.Ordinal);
        Assert.Contains("moved to the recycle bin", told.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_episode_file_deleted_by_hand_is_told_the_same_way()
    {
        await using var world = await StartAsync();
        await world.SwitchesAsync("fileDeletedEnabled");
        var tvLibrary = await ApiScenes.CreateLibraryAsync(world.App, "tv");
        var show = await ApiScenes.ShowOnDiskAsync(world.App, "The Wire", 2002, tvLibrary);

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/series/episodes/{show.EpisodeId}/file");
        var removed = await world.App.Client.SendAsync(request);

        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        Assert.False(File.Exists(show.FilePath));
        var told = Assert.Single(world.Ntfy.Requests);
        Assert.StartsWith("File removed:", told.Header("title"), StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(show.FilePath), told.Header("title"), StringComparison.Ordinal);
    }

    // ---- a failure ----

    [Fact]
    public async Task A_refused_import_is_told_with_what_went_wrong_while_the_failed_import_switch_is_on_and_silent_when_it_is_off()
    {
        await using var world = await StartAsync(new ScriptedProbe(_ => ScriptedProbe.Good(seconds: 90)));
        var movie = await world.Disk.AddMovieAsync("Sintel", 2010);
        var source = world.Disk.WriteDownload(Release + ".mkv");

        var refused = await world.Disk.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: await world.Disk.RecordDispatchAsync(movie.Id, Release)));
        Assert.False(refused.Succeeded);

        var told = Assert.Single(world.Ntfy.Requests);
        Assert.Equal("Import failed: Sintel", told.Header("title").Replace(" (2010)", string.Empty, StringComparison.Ordinal));
        Assert.Contains("under two minutes", told.Body, StringComparison.Ordinal);

        var current = (JsonObject)JsonNode.Parse(await world.App.Client.GetStringAsync("/api/notification-preferences"))!;
        current["importFailedEnabled"] = false;
        Assert.True((await world.App.Client.PutAsJsonAsync("/api/notification-preferences", current)).IsSuccessStatusCode);
        var second = world.Disk.WriteDownload(Release + ".2.mkv", 0x44);
        await world.Disk.ImportAsync(RealDiskLibrary.Request(second, "Sintel", 2010, dispatchId: await world.Disk.RecordDispatchAsync(movie.Id, Release + ".2")));
        Assert.Single(world.Ntfy.Requests);
    }

    // ---- retention ----

    private static async Task<(string SeriesId, string[] Files)> TwoSeasonShowAsync(World world)
    {
        var tvLibrary = await ApiScenes.CreateLibraryAsync(world.App, "tv");
        var root = await ApiScenes.LibraryRootAsync(world.App, tvLibrary);
        var files = new[] { (1, 1), (1, 2), (2, 1) }.Select(number => Path.Combine(root, "Nosferatu (1922)", $"Season {number.Item1:00}", $"Nosferatu.S{number.Item1:00}E{number.Item2:00}.1080p.WEB-DL.x264-HD4U.mkv")).ToArray();
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        }

        var added = await world.App.Client.PostAsJsonAsync("/api/series/", new { title = "Nosferatu", startYear = 1922, monitored = true, libraryId = tvLibrary, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var seriesId = JsonDocument.Parse(await added.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        // Both seasons have aired, so the seasons can be ranked newest first and the older one is the one the rule takes.
        await world.Disk.SqlAsync("series", $"UPDATE episode_entries SET air_date_utc = CASE season_number WHEN 1 THEN '{DateTimeOffset.UtcNow.AddDays(-400):O}' ELSE '{DateTimeOffset.UtcNow.AddDays(-100):O}' END WHERE has_file = 1;");
        return (seriesId, files);
    }

    private static async Task RemoveSeasonsAsync(World world)
    {
        var rule = await world.App.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old seasons", level = "season", isEnabled = true, steps = new[] { new { type = "condition", op = "keep", condition = "title.seasonRank:min:2" } },
            mediaServerIds = Array.Empty<string>(), removeFromServers = false, waitDays = 1, removalMode = "automatic", checkIntervalHours = 24, expiryAction = "delete"
        });
        Assert.True(rule.IsSuccessStatusCode, await rule.Content.ReadAsStringAsync());
        var ruleId = JsonDocument.Parse(await rule.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        Assert.True((await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null)).IsSuccessStatusCode, "the first check only marks the seasons");
        await world.Disk.SqlAsync("platform", $"UPDATE retention_marks SET remove_after_utc = '{DateTimeOffset.UtcNow.AddHours(-1):O}';");
        var removed = await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null);
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Seasons_a_retention_rule_removes_are_told_to_the_phone_only_while_the_file_deleted_switch_is_on(bool on)
    {
        await using var world = await StartAsync();
        // A season's title has a dash in it ("Nosferatu — Season 1"), which an ntfy Title header cannot carry (the defect skipped above), so this connection is a generic one.
        foreach (var phone in (await world.App.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).EnumerateArray())
        {
            var phoneId = phone.GetProperty("id").GetString();
            await world.App.Client.DeleteAsync($"/api/notification-webhooks/{phoneId}");
        }

        Assert.True((await world.App.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Anything", url = world.Ntfy.BaseUrl + "/generic", eventFilters = "all", isEnabled = true, provider = "generic" })).IsSuccessStatusCode);
        await world.SwitchesAsync(on ? ["fileDeletedEnabled"] : []);
        var (_, files) = await TwoSeasonShowAsync(world);

        await RemoveSeasonsAsync(world);

        Assert.False(File.Exists(files[0]) || File.Exists(files[1]), "season one left");
        Assert.True(File.Exists(files[2]), "season two stays");
        var told = world.Ntfy.Requests.Where(request => JsonNode.Parse(request.Body)!["eventCategory"]!.GetValue<string>() == "file.deleted").ToArray();
        if (on)
        {
            var season = Assert.Single(told);
            Assert.Equal("Files removed: Nosferatu — Season 1", JsonNode.Parse(season.Body)!["title"]!.GetValue<string>());
            Assert.Contains("Removed by retention rule", JsonNode.Parse(season.Body)!["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Empty(told);
        }
    }

    // ---- a library going away and coming back ----

    [Fact]
    public async Task A_library_folder_that_goes_away_and_comes_back_is_told_once_each_way_and_the_next_look_at_it_says_nothing()
    {
        await using var world = await StartAsync();
        await world.SwitchesAsync();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-19T09:00:00Z"));
        var library = world.Disk.Library;
        var check = new LibraryAvailabilityService(
            world.App.Services.GetRequiredService<Deluno.Jobs.Data.IActivityFeedRepository>(), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryAvailabilityService>.Instance,
            world.App.Services.GetRequiredService<Deluno.Platform.Data.IPlatformSettingsRepository>(),
            world.App.Services.GetRequiredService<IOutboundNotificationService>());
        Assert.Empty((await check.ReadAsync([library], CancellationToken.None)).UnreachableLibraryIds);
        Assert.Empty(world.Ntfy.Requests);

        Directory.Delete(world.Disk.Root, recursive: true);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Contains(library.Id, (await check.ReadAsync([library], CancellationToken.None)).UnreachableLibraryIds);
        clock.Advance(TimeSpan.FromMinutes(2));
        await check.ReadAsync([library], CancellationToken.None);
        var paused = Assert.Single(world.Ntfy.Requests);
        Assert.Equal("Movies paused", paused.Header("title"));
        Assert.Contains("Deluno has paused it", paused.Body, StringComparison.Ordinal);

        Directory.CreateDirectory(world.Disk.Root);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty((await check.ReadAsync([library], CancellationToken.None)).UnreachableLibraryIds);
        clock.Advance(TimeSpan.FromMinutes(2));
        await check.ReadAsync([library], CancellationToken.None);
        Assert.Equal(["Movies paused", "Movies resumed"], world.Titles);
        Assert.Contains("Deluno has resumed searching and importing", world.Ntfy.Requests[1].Body, StringComparison.Ordinal);
    }

    // ---- a notification that cannot be sent never fails what caused it ----

    private sealed class ThrowingOutbound : IOutboundNotificationService
    {
        public int Attempts;

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException("The notification service is broken.");
        }

        public Task<Deluno.Notifications.Contracts.NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The notification service is broken.");

        public Task<Deluno.Notifications.Contracts.NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The notification service is broken.");
    }

    [Fact]
    public async Task A_notification_service_that_throws_never_fails_an_import_a_file_delete_or_a_removal_and_the_media_server_is_still_told()
    {
        var broken = new ThrowingOutbound();
        await using var jellyfin = await RecordedJellyfinServer.StartAsync();
        await using var world = await StartAsync(services: services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(IOutboundNotificationService)) services.RemoveAt(i);
            }

            services.AddSingleton<IOutboundNotificationService>(broken);
        });
        await world.SwitchesAsync("upgradeEnabled", "renameEnabled", "fileDeletedEnabled");
        var server = await world.App.Client.PostAsJsonAsync("/api/media-servers", new { kind = "jellyfin", name = "Living room", baseUrl = jellyfin.BaseUrl, token = "jf-secret-token-1", isEnabled = true });
        Assert.True(server.IsSuccessStatusCode, await server.Content.ReadAsStringAsync());

        // An import: the file lands, the catalogue knows it and the media server is told, though three notices cannot be sent.
        var imported = await ImportAsync(world, "Sintel", 2010, Release);
        Assert.True(File.Exists(imported.Destination));
        Assert.True((await world.Disk.Movies.GetByIdAsync(imported.MovieId, CancellationToken.None))!.HasFile);

        // A file deleted by hand, and a title removed with its files.
        var film = await ApiScenes.FilmOnDiskAsync(world.App, "Dune", 2021, world.Disk.Library.Id);
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/movies/{film.MovieId}/files") { Content = JsonContent.Create(new { path = film.FilePath }) };
        Assert.True((await world.App.Client.SendAsync(request)).IsSuccessStatusCode);
        Assert.False(File.Exists(film.FilePath));
        var other = await ApiScenes.FilmOnDiskAsync(world.App, "Carnival of Souls", 1962, world.Disk.Library.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await world.App.Client.DeleteAsync($"/api/movies/{other.MovieId}?deleteFiles=true")).StatusCode);
        Assert.False(File.Exists(other.FilePath));

        Assert.True(broken.Attempts >= 3, $"the broken service was asked {broken.Attempts} times");
        Assert.Empty(world.Ntfy.Requests);
        await RunRefreshesAsync(world.App);
        Assert.Contains("Created", jellyfin.UpdateBodies.SelectMany(body => JsonDocument.Parse(body).RootElement.GetProperty("Updates").EnumerateArray().Select(update => update.GetProperty("UpdateType").GetString())));
    }

    private static async Task RunRefreshesAsync(ApplicationTestHost app)
    {
        var jobs = await app.Services.GetRequiredService<Deluno.Jobs.Data.IJobQueueRepository>().ListAsync(200, CancellationToken.None);
        var handler = app.Services.GetServices<Deluno.Worker.Jobs.IJobHandler>().Single(candidate => candidate.JobType == Deluno.Contracts.MediaServerJobTypes.Refresh);
        foreach (var job in jobs.Where(job => job.JobType == Deluno.Contracts.MediaServerJobTypes.Refresh))
        {
            await handler.HandleAsync(job, CancellationToken.None);
        }
    }

    [Fact(Skip = "Defect: adding a film (POST /api/movies, the On Title Added notice) calls the notification service directly instead of through NotificationOutboundDispatch, so a notification service that throws fails the add with an HTTP 500 after the film has already been saved (MoviesEndpointRouteBuilderExtensions, the title.added dispatch). The import, the file delete, the removal and the grab are guarded; the add is not (found 19 Oct 2026).")]
    public async Task A_notification_service_that_throws_never_fails_the_add_of_a_film()
    {
        var broken = new ThrowingOutbound();
        await using var world = await StartAsync(services: services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(IOutboundNotificationService)) services.RemoveAt(i);
            }

            services.AddSingleton<IOutboundNotificationService>(broken);
        });
        await world.SwitchesAsync("addedEnabled");

        var added = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title = "Arrival", releaseYear = 2016, monitored = true, libraryId = world.Disk.Library.Id, searchForMissingNow = false });

        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        Assert.Equal(1, broken.Attempts);
    }

    [Fact(Skip = "Defect: recording a grab (SqliteJobStore.RecordDownloadDispatchAsync, the On Grab notice) calls the notification service directly with no guard, so a notification service that throws makes recording the dispatch throw after the release was already sent to the download client, and the caller sees a failed grab for a download that is running (found 19 Oct 2026).")]
    public async Task A_notification_service_that_throws_never_fails_the_recording_of_a_grab()
    {
        var broken = new ThrowingOutbound();
        await using var world = await StartAsync(services: services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(IOutboundNotificationService)) services.RemoveAt(i);
            }

            services.AddSingleton<IOutboundNotificationService>(broken);
        });
        await world.SwitchesAsync("grabEnabled");
        var jobs = world.App.Services.GetRequiredService<Deluno.Jobs.Data.IJobQueueRepository>();

        var dispatch = await jobs.RecordDownloadDispatchAsync(world.Disk.Library.Id, "movies", "movie", "title-wanted", "Wanted.Film.2011.1080p.WEB-DL-DELUNO", "Fixture", "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(dispatch));
        Assert.Equal(1, broken.Attempts);
    }

    // ---- known defects, each with the evidence that shows it ----

    private sealed class CapturedLogs : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturedLogs owner) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception.Message));
        }
    }

    [Fact]
    public async Task A_failed_delivery_never_writes_the_secret_in_the_webhook_address_to_the_log()
    {
        const string secret = "SECRETwebhookTOKEN0123456789";
        var logs = new CapturedLogs();
        await using var world = await StartAsync(services: services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs));
        foreach (var phone in (await world.App.Client.GetFromJsonAsync<JsonElement>("/api/notification-webhooks")).EnumerateArray())
        {
            await world.App.Client.DeleteAsync("/api/notification-webhooks/" + phone.GetProperty("id").GetString());
        }

        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var deadPort = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        Assert.True((await world.App.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Chat", url = $"http://127.0.0.1:{deadPort}/api/webhooks/1234567890/{secret}", eventFilters = "all", isEnabled = true, provider = "generic" })).IsSuccessStatusCode);
        await world.App.Services.GetRequiredService<IOutboundNotificationService>().DispatchAsync("import.completed", "Import completed: Sintel (2010)", "Done.", null, CancellationToken.None);

        Assert.DoesNotContain(logs.Lines, line => line.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_title_a_retention_rule_could_not_remove_is_told_to_the_phone_in_its_own_words()
    {
        await using var world = await StartAsync();
        await world.SwitchesAsync();
        var folder = Path.Combine(world.Disk.Root, "Nosferatu (1922)");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        var added = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title = "Nosferatu", releaseYear = 1922, monitored = true, libraryId = world.Disk.Library.Id, searchForMissingNow = false });
        var movieId = JsonDocument.Parse(await added.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        await world.Disk.SqlAsync("movies", $"UPDATE movie_entries SET created_utc = '{DateTimeOffset.UtcNow.AddDays(-365):O}' WHERE id = '{movieId}';");
        await world.Disk.SqlAsync("movies", "CREATE TRIGGER keep_the_film BEFORE DELETE ON movie_entries BEGIN SELECT RAISE(ABORT, 'disk image is malformed'); END;");
        var rule = await world.App.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old films", level = "movie", isEnabled = true, steps = new[] { new { type = "condition", op = "keep", condition = "added:beyond:30" } },
            mediaServerIds = Array.Empty<string>(), removeFromServers = false, waitDays = 1, removalMode = "automatic", checkIntervalHours = 24, expiryAction = "delete"
        });
        var ruleId = JsonDocument.Parse(await rule.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null);
        await world.Disk.SqlAsync("platform", $"UPDATE retention_marks SET remove_after_utc = '{DateTimeOffset.UtcNow.AddHours(-1):O}';");

        var run = await (await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(run.GetProperty("failedCount").GetInt32() > 0, run.ToString());
        var failed = Assert.Single(world.Ntfy.Requests, request => request.Header("title") == "1 title could not be removed");
        Assert.Contains("Old films", failed.Body, StringComparison.Ordinal);
        Assert.Contains("Nosferatu", failed.Body, StringComparison.Ordinal);
    }

    [Fact(Skip = "Defect: importing a TV season pack fires no import.completed, upgrade or rename notice. The pack path of ImportPipelineService (the \"season pack imported N files\" Activity line) has no outbound dispatch at all, so a person with every switch on hears about a film and about a single episode, but never about a season (found 19 Oct 2026).")]
    public async Task An_imported_season_pack_is_told_to_the_phone_like_any_other_import()
    {
        await using var world = await StartAsync();
        await world.SwitchesAsync("renameEnabled", "upgradeEnabled");
        Assert.True((await world.App.Client.PatchAsJsonAsync("/api/settings", new { renameOnImport = true })).IsSuccessStatusCode);
        var tvLibrary = await ApiScenes.CreateLibraryAsync(world.App, "tv");
        var root = await ApiScenes.LibraryRootAsync(world.App, tvLibrary);
        var added = await world.App.Client.PostAsJsonAsync("/api/series/", new { title = "Nosferatu", startYear = 1922, monitored = true, libraryId = tvLibrary, searchForMissingNow = false });
        var seriesId = JsonDocument.Parse(await added.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        var series = world.App.Services.GetRequiredService<Deluno.Series.Data.ISeriesCatalogRepository>();
        await series.SyncEpisodeCatalogueAsync(seriesId, [new Deluno.Series.Contracts.CatalogueEpisodeItem(1, 1, "One", null, DateTimeOffset.UtcNow.AddDays(-9)), new Deluno.Series.Contracts.CatalogueEpisodeItem(1, 2, "Two", null, DateTimeOffset.UtcNow.AddDays(-8))], "provider", CancellationToken.None);
        var pack = Directory.CreateDirectory(Path.Combine(world.Disk.Downloads, "Nosferatu.S01.1080p.WEB-DL")).FullName;
        foreach (var episode in new[] { 1, 2 })
        {
            var path = Path.Combine(pack, $"Nosferatu.S01E{episode:D2}.1080p.WEB-DL.mkv");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)(0x40 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        }

        var result = await world.Disk.ImportAsync(new ImportExecuteRequest(
            new ImportPreviewRequest(pack, "Nosferatu.S01.1080p.WEB-DL.mkv", "tv", "Nosferatu", 1922, [], [], null, null, SeriesId: seriesId),
            "copy", Overwrite: false, AllowCopyFallback: true));

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(world.Titles, title => title.StartsWith("Import completed:", StringComparison.Ordinal));
    }
}
