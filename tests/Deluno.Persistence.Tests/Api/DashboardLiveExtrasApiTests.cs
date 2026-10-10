using System.Net;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// GET /api/dashboard/live-extras (#1096): what the Live screen shows beyond the
/// summary, read from stored rows only.
/// </summary>
public sealed class DashboardLiveExtrasApiTests
{
    private static string Midnight() => Uri.EscapeDataString(DateTimeOffset.UtcNow.Date.ToString("O"));

    private static async Task<JsonElement> GetAsync(ApplicationTestHost app)
    {
        var response = await app.Client.GetAsync($"/api/dashboard/live-extras?sinceUtc={Midnight()}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task An_empty_install_answers_with_empty_lists_and_zeros()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var live = await GetAsync(app);

        Assert.Equal(0, live.GetProperty("arrivals").GetArrayLength());
        Assert.Equal(0, live.GetProperty("importsToday").GetInt32());
        Assert.Equal(0, live.GetProperty("protected").GetProperty("refusedToday").GetInt32());
        Assert.Equal(0, live.GetProperty("requests").GetArrayLength());
        Assert.Equal(0, live.GetProperty("retentionReclaimBytes").GetInt64());
    }

    [Fact]
    public async Task Today_s_imports_upgrades_refusals_requests_and_reclaimable_space_come_from_the_stored_rows()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var yesterday = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");

        await ExecuteAsync(databases, DelunoDatabaseNames.Jobs,
            $$"""
            INSERT INTO activity_events (id, category, message, details_json, related_entity_type, related_entity_id, created_utc)
            VALUES ('a1', 'filesystem.import.completed', 'Imported Sintel', '{"ReleaseNameEvidence":"Sintel.2010.1080p","NewQuality":"Bluray 1080p","PreviousQuality":"WEB 720p","Upgraded":true,"QualityMet":false,"FileSizeBytes":4000}', 'movie', 'm1', '{{now}}'),
                   ('a2', 'filesystem.import.completed', 'Imported Moon', '{"ReleaseNameEvidence":"Moon.2009.720p","NewQuality":"WEB 720p","Upgraded":false,"QualityMet":true,"FileSizeBytes":1000}', 'movie', 'm2', '{{now}}'),
                   ('a3', 'filesystem.import.completed', 'Imported Old', NULL, 'movie', 'm3', '{{yesterday}}');
            INSERT INTO blocked_releases (id, release_key, release_name, indexer_name, media_type, reason_code, reason, blocked_utc)
            VALUES ('b1', 'k1', 'Fake.Film.2020', 'Idx', 'movies', 'noVideoStream', 'No video in it', '{{now}}'),
                   ('b2', 'k2', 'Slow.Film.2020', 'Idx', 'movies', 'downloadStalled', 'It stalled', '{{now}}'),
                   ('b3', 'k3', 'Old.Film.2020', 'Idx', 'movies', 'noVideoStream', 'No video', '{{yesterday}}');
            """);
        await ExecuteAsync(databases, DelunoDatabaseNames.Platform,
            $$"""
            INSERT INTO retention_marks (rule_id, entity_id, level, title, size_bytes, first_matched_utc, remove_after_utc, state, updated_utc)
            VALUES ('r', 'm1', 'movie', 'Leaving A', 5000, '{{now}}', '{{now}}', 'leaving', '{{now}}'),
                   ('r', 'm2', 'movie', 'Ready B', 700, '{{now}}', '{{now}}', 'ready', '{{now}}'),
                   ('r', 'm3', 'movie', 'Kept C', 9000, '{{now}}', '{{now}}', 'kept', '{{now}}');
            INSERT INTO seerr_acquisitions (id, connection_id, connection_name, media_type, entity_id, title, seerr_media_id, status, message, created_utc, updated_utc)
            VALUES ('q1', 'c', 'Seerr', 'movie', 'm1', 'Sintel', 1, 'added', NULL, '{{now}}', '{{now}}');
            """);

        var live = await GetAsync(app);

        Assert.Equal(2, live.GetProperty("importsToday").GetInt32());
        Assert.Equal(5000, live.GetProperty("importedBytesToday").GetInt64());
        var upgrades = live.GetProperty("upgrades");
        Assert.Equal(1, upgrades.GetArrayLength());
        Assert.Equal("WEB 720p", upgrades[0].GetProperty("previousQuality").GetString());
        Assert.Equal("Bluray 1080p", upgrades[0].GetProperty("quality").GetString());
        // The poster bar: an upgrade that can still be improved is not "quality met"; a film that reached its cutoff is; an old event that never said is unknown.
        Assert.False(upgrades[0].GetProperty("qualityMet").GetBoolean());
        var arrivals = live.GetProperty("arrivals");
        var moon = arrivals.EnumerateArray().First(item => item.GetProperty("entityId").GetString() == "m2");
        Assert.True(moon.GetProperty("qualityMet").GetBoolean());
        var protectedYou = live.GetProperty("protected");
        Assert.Equal(2, protectedYou.GetProperty("refusedToday").GetInt32());
        Assert.Equal(1, protectedYou.GetProperty("unsafeToday").GetInt32());
        Assert.Equal(2, protectedYou.GetProperty("latest").GetArrayLength());
        Assert.Equal(5700, live.GetProperty("retentionReclaimBytes").GetInt64());
        Assert.Equal("added", live.GetProperty("requests")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Recent_arrivals_are_the_newest_imports_of_the_last_seven_days_when_nothing_came_in_today()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var yesterday = DateTimeOffset.UtcNow.AddHours(-30).ToString("O");
        var threeDays = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");
        var tenDays = DateTimeOffset.UtcNow.AddDays(-10).ToString("O");

        await ExecuteAsync(databases, DelunoDatabaseNames.Jobs,
            $$"""
            INSERT INTO activity_events (id, category, message, details_json, related_entity_type, related_entity_id, created_utc)
            VALUES ('r1', 'filesystem.import.completed', 'Imported Moon', '{"ReleaseNameEvidence":"Moon.2009.720p","NewQuality":"WEB 720p","FileSizeBytes":1000}', 'movie', 'm2', '{{threeDays}}'),
                   ('r2', 'filesystem.import.completed', 'Imported Sintel', '{"ReleaseNameEvidence":"Sintel.2010.1080p","NewQuality":"Bluray 1080p","FileSizeBytes":4000}', 'movie', 'm1', '{{yesterday}}'),
                   ('r3', 'filesystem.import.completed', 'Imported Old', NULL, 'movie', 'm3', '{{tenDays}}');
            """);

        var live = await GetAsync(app);

        Assert.Equal(0, live.GetProperty("arrivals").GetArrayLength());
        var recent = live.GetProperty("recentArrivals");
        Assert.Equal(2, recent.GetArrayLength());
        Assert.Equal("m1", recent[0].GetProperty("entityId").GetString());
        Assert.Equal("Bluray 1080p", recent[0].GetProperty("quality").GetString());
        Assert.Equal("m2", recent[1].GetProperty("entityId").GetString());
    }

    [Fact]
    public async Task An_empty_install_has_every_turned_down_group_at_zero_and_no_connections()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var live = await GetAsync(app);

        var turnedDown = live.GetProperty("turnedDown");
        Assert.Equal(0, turnedDown.GetProperty("total").GetInt32());
        Assert.Equal(
            ["language", "notBetter", "notQuality", "tooBig", "tooSmall", "fewSeeders", "fake", "rules", "tooOld", "blocklisted", "other"],
            turnedDown.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetProperty("group").GetString()!).ToArray());
        Assert.All(turnedDown.GetProperty("reasons").EnumerateArray(), reason => Assert.Equal(0, reason.GetProperty("count").GetInt32()));
        Assert.Equal(0, turnedDown.GetProperty("latest").GetArrayLength());
        Assert.Equal(0, live.GetProperty("cleanedUpToday").GetInt32());
        Assert.Equal(0, live.GetProperty("connections").GetArrayLength());
    }

    [Fact]
    public async Task A_row_stored_as_other_is_regrouped_from_its_raw_reason_when_it_is_read()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var recorder = app.Services.GetRequiredService<Deluno.Jobs.Data.ITurnedDownReleaseRecorder>();
        await recorder.RecordAsync("Sintel", "movie", "m1",
        [
            new Deluno.Jobs.Data.TurnedDownRelease("Sintel.2010.2160p", "other", "Bluray 2160p is not one of the qualities this profile allows (Bluray 1080p).", "Idx"),
            new Deluno.Jobs.Data.TurnedDownRelease("Sintel.2010.x265", "other", "Release name is missing required term 'REMUX'.", "Idx"),
            new Deluno.Jobs.Data.TurnedDownRelease("Sintel.2010.Blocked", "other", "On your blocklist.", "Idx"),
            new Deluno.Jobs.Data.TurnedDownRelease("Sintel.2010.Odd", "other", null, "Idx")
        ], CancellationToken.None);

        var turnedDown = (await GetAsync(app)).GetProperty("turnedDown");

        var counts = turnedDown.GetProperty("reasons").EnumerateArray()
            .ToDictionary(reason => reason.GetProperty("group").GetString()!, reason => reason.GetProperty("count").GetInt32());
        Assert.Equal(1, counts["notQuality"]);
        Assert.Equal(1, counts["rules"]);
        Assert.Equal(1, counts["blocklisted"]);
        Assert.Equal(1, counts["other"]);
        Assert.Contains(turnedDown.GetProperty("latest").EnumerateArray(), item => item.GetProperty("release").GetString() == "Sintel.2010.2160p" && item.GetProperty("group").GetString() == "notQuality");
    }

    [Fact]
    public async Task Turned_down_today_counts_each_group_lists_the_newest_twenty_and_ignores_yesterday()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var recorder = app.Services.GetRequiredService<Deluno.Jobs.Data.ITurnedDownReleaseRecorder>();

        await recorder.RecordAsync("Sintel", "movie", "m1",
        [
            new Deluno.Jobs.Data.TurnedDownRelease("Cam.Sintel", "fake", "CAM", "Idx"),
            new Deluno.Jobs.Data.TurnedDownRelease("Dub.Sintel", "language", "audio.language", "Idx"),
            new Deluno.Jobs.Data.TurnedDownRelease("Dub.Sintel", "language", "audio.language", "Other")
        ], CancellationToken.None);
        await recorder.RecordAsync("Moon", "series", "s1",
        [
            new Deluno.Jobs.Data.TurnedDownRelease("Same.Moon", "notBetter", "equivalent", "Idx"),
            new Deluno.Jobs.Data.TurnedDownRelease("Huge.Moon", "tooBig", "size", "Idx")
        ], CancellationToken.None);
        await ExecuteAsync(databases, DelunoDatabaseNames.Jobs,
            $"INSERT INTO turned_down_releases (id, searched_utc, release_name, reason_group) VALUES ('old', '{DateTimeOffset.UtcNow.AddDays(-3):O}', 'Old.Release', 'other');");

        var live = await GetAsync(app);

        var turnedDown = live.GetProperty("turnedDown");
        Assert.Equal(4, turnedDown.GetProperty("total").GetInt32());
        var counts = turnedDown.GetProperty("reasons").EnumerateArray()
            .ToDictionary(reason => reason.GetProperty("group").GetString()!, reason => reason.GetProperty("count").GetInt32());
        Assert.Equal(1, counts["language"]);
        Assert.Equal(1, counts["notBetter"]);
        Assert.Equal(1, counts["tooBig"]);
        Assert.Equal(0, counts["fewSeeders"]);
        Assert.Equal(1, counts["fake"]);
        Assert.Equal(0, counts["other"]);
        Assert.Equal(
            "Not in a language you want",
            turnedDown.GetProperty("reasons")[0].GetProperty("label").GetString());

        var latest = turnedDown.GetProperty("latest");
        Assert.Equal(4, latest.GetArrayLength());
        // Newest search first: Moon's, then Sintel's.
        Assert.Equal("Moon", latest[0].GetProperty("title").GetString());
        Assert.Equal("series", latest[0].GetProperty("entityType").GetString());
        Assert.Equal("s1", latest[0].GetProperty("entityId").GetString());
        Assert.Equal("Sintel", latest[3].GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(latest[0].GetProperty("atUtc").GetString()));
        Assert.Equal("Bigger than your size limit", latest.EnumerateArray().First(item => item.GetProperty("group").GetString() == "tooBig").GetProperty("label").GetString());
    }

    [Fact]
    public async Task The_same_release_met_in_five_searches_counts_once_and_the_hour_repeat_is_not_stored()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var recorder = app.Services.GetRequiredService<Deluno.Jobs.Data.ITurnedDownReleaseRecorder>();
        var fresh = new List<int>();
        for (var run = 0; run < 5; run++)
        {
            var isNew = await recorder.RecordAsync("Sintel", "movie", "m1",
                [new Deluno.Jobs.Data.TurnedDownRelease("Same.Release.1080p", "tooBig", "size", "Idx")], CancellationToken.None);
            fresh.Add(isNew.Count);
        }

        Assert.Equal([1, 0, 0, 0, 0], fresh);
        var turnedDown = (await GetAsync(app)).GetProperty("turnedDown");
        Assert.Equal(1, turnedDown.GetProperty("total").GetInt32());
        Assert.Equal(1, turnedDown.GetProperty("latest").GetArrayLength());
        await using var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM turned_down_releases;";
        Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task A_release_counts_under_its_latest_reason_today_and_is_listed_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var recorder = app.Services.GetRequiredService<Deluno.Jobs.Data.ITurnedDownReleaseRecorder>();
        // Turned down for size earlier today, then for fake this search. "Today" starts at local midnight, so
        // in the first ninety minutes after it, ninety minutes ago was yesterday: the earlier row goes halfway
        // between midnight and now instead (CI runs on UTC and once failed at 00:34).
        var now = DateTimeOffset.Now;
        var localMidnight = new DateTimeOffset(now.Date, now.Offset);
        var earlier = now.AddMinutes(-90) > localMidnight ? now.AddMinutes(-90) : localMidnight + (now - localMidnight) / 2;
        await ExecuteAsync(databases, DelunoDatabaseNames.Jobs,
            $"INSERT INTO turned_down_releases (id, searched_utc, release_name, reason_group, raw_reason, indexer_name) VALUES ('a', '{earlier.ToUniversalTime():O}', 'Moving.Release', 'tooBig', 'size', 'Idx');");
        var isNew = await recorder.RecordAsync("Sintel", "movie", "m1",
            [new Deluno.Jobs.Data.TurnedDownRelease("moving.release", "fake", "CAM", "Idx")], CancellationToken.None);

        Assert.Empty(isNew);
        var turnedDown = (await GetAsync(app)).GetProperty("turnedDown");
        var counts = turnedDown.GetProperty("reasons").EnumerateArray()
            .ToDictionary(reason => reason.GetProperty("group").GetString()!, reason => reason.GetProperty("count").GetInt32());
        Assert.Equal(1, turnedDown.GetProperty("total").GetInt32());
        Assert.Equal(1, counts["fake"]);
        Assert.Equal(0, counts["tooBig"]);
        Assert.Equal(1, turnedDown.GetProperty("latest").GetArrayLength());
    }

    [Fact]
    public async Task The_pushed_turned_down_event_flags_only_releases_new_today()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var recorder = app.Services.GetRequiredService<Deluno.Jobs.Data.ITurnedDownReleaseRecorder>();
        await recorder.RecordAsync("Sintel", "movie", "m1", [new Deluno.Jobs.Data.TurnedDownRelease("Seen.Before", "tooBig", "size", "Idx")], CancellationToken.None);

        var isNew = await recorder.RecordAsync("Sintel", "movie", "m1",
            [new Deluno.Jobs.Data.TurnedDownRelease("Seen.Before", "tooBig", "size", "Idx"), new Deluno.Jobs.Data.TurnedDownRelease("Brand.New", "fake", "CAM", "Idx")], CancellationToken.None);
        var events = new[] { "Seen.Before", "Brand.New" }
            .Select(name => new Deluno.Contracts.TurnedDownItemEvent(name, "other", "x", isNew.Contains(name))).ToArray();

        Assert.False(events[0].NewToday);
        Assert.True(events[1].NewToday);
    }

    [Fact]
    public async Task Only_twenty_turned_down_releases_are_listed_though_all_are_counted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var recorder = app.Services.GetRequiredService<Deluno.Jobs.Data.ITurnedDownReleaseRecorder>();
        await recorder.RecordAsync("Sintel", "movie", "m1",
            Enumerable.Range(1, 30).Select(index => new Deluno.Jobs.Data.TurnedDownRelease($"Release.{index}", "other", null, "Idx")).ToArray(),
            CancellationToken.None);

        var turnedDown = (await GetAsync(app)).GetProperty("turnedDown");

        Assert.Equal(30, turnedDown.GetProperty("total").GetInt32());
        Assert.Equal(20, turnedDown.GetProperty("latest").GetArrayLength());
    }

    [Fact]
    public async Task A_seeding_clean_up_today_is_counted_and_one_from_before_is_not()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var before = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");
        await ExecuteAsync(databases, DelunoDatabaseNames.Jobs,
            $$"""
            INSERT INTO activity_events (id, category, message, details_json, related_entity_type, related_entity_id, created_utc)
            VALUES ('c1', 'download.sharing.reclaimed', 'Removed A', NULL, 'movie', 'm1', '{{now}}'),
                   ('c2', 'download.sharing.reclaimed', 'Removed B', NULL, 'movie', 'm2', '{{now}}'),
                   ('c3', 'download.sharing.reclaimed', 'Removed C', NULL, 'movie', 'm3', '{{before}}'),
                   ('c4', 'download.sharing.stopped', 'Stopped D', NULL, 'movie', 'm4', '{{now}}');
            """);

        Assert.Equal(2, (await GetAsync(app)).GetProperty("cleanedUpToday").GetInt32());
    }

    [Fact]
    public async Task Connections_list_each_processor_media_server_seerr_subtitle_provider_and_list_family_with_what_it_did_today()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var before = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");
        await ExecuteAsync(databases, DelunoDatabaseNames.Platform,
            $$"""
            INSERT INTO processor_connections (id, name, provider, submission_url, is_enabled, health_status, created_utc, updated_utc)
            VALUES ('p1', 'Weir', 'generic', 'http://w', 1, 'healthy', '{{now}}', '{{now}}'),
                   ('p2', 'FileFlows', 'generic', 'http://f', 0, 'unreachable', '{{now}}', '{{now}}');
            INSERT INTO processor_handoffs (id, library_id, media_type, client_id, queue_item_id, release_name, source_path, source_key, processor_name, processor_connection_id, status, created_utc, updated_utc)
            VALUES ('h1', 'lib', 'movies', 'c', 'q1', 'A', '/a', 'ka', 'Weir', 'p1', 'started', '{{now}}', '{{now}}'),
                   ('h2', 'lib', 'movies', 'c', 'q2', 'B', '/b', 'kb', 'Weir', 'p1', 'completed', '{{now}}', '{{now}}'),
                   ('h3', 'lib', 'movies', 'c', 'q3', 'C', '/c', 'kc', 'Weir', 'p1', 'completed', '{{before}}', '{{before}}');
            INSERT INTO media_servers (id, kind, name, base_url, is_enabled, health_status, created_utc, updated_utc)
            VALUES ('ms1', 'plex', 'Plex', 'http://p', 1, 'healthy', '{{now}}', '{{now}}');
            INSERT INTO seerr_servers (id, name, base_url, is_enabled, health_status, created_utc, updated_utc)
            VALUES ('sr1', 'Seerr', 'http://s', 1, 'healthy', '{{now}}', '{{now}}');
            INSERT INTO seerr_acquisitions (id, connection_id, connection_name, media_type, entity_id, title, seerr_media_id, status, message, created_utc, updated_utc)
            VALUES ('q1', 'sr1', 'Seerr', 'movie', 'm1', 'Sintel', 1, 'added', NULL, '{{now}}', '{{now}}'),
                   ('q2', 'sr1', 'Seerr', 'movie', 'm2', 'Moon', 2, 'added', NULL, '{{now}}', '{{now}}'),
                   ('q3', 'sr1', 'Seerr', 'movie', 'm3', 'Old', 3, 'added', NULL, '{{before}}', '{{before}}');
            INSERT INTO subtitle_providers (id, provider_key, name, is_enabled, health_status, created_utc, updated_utc)
            VALUES ('sp1', 'opensubtitles', 'OpenSubtitles', 1, 'healthy', '{{now}}', '{{now}}');
            INSERT INTO intake_sources (id, name, provider, feed_url, media_type, is_enabled, last_sync_status, created_utc, updated_utc)
            VALUES ('l1', 'Trending', 'trakt-trending', 'x', 'movies', 1, 'ok', '{{now}}', '{{now}}'),
                   ('l2', 'Watchlist', 'trakt', 'x', 'movies', 1, 'error', '{{now}}', '{{now}}'),
                   ('l3', 'Popular', 'tmdb-popular', 'x', 'movies', 1, 'never', '{{now}}', '{{now}}');
            """);
        await ExecuteAsync(databases, DelunoDatabaseNames.Jobs,
            $$"""
            INSERT INTO connection_uses (id, kind, connection_id, used_utc)
            VALUES ('u1', 'mediaServer', 'ms1', '{{now}}'),
                   ('u2', 'mediaServer', 'ms1', '{{now}}'),
                   ('u3', 'mediaServer', 'ms1', '{{before}}'),
                   ('u4', 'subtitles', 'sp1', '{{now}}');
            """);

        var connections = (await GetAsync(app)).GetProperty("connections").EnumerateArray().ToArray();

        JsonElement Find(string kind, string id) => connections.Single(item => item.GetProperty("kind").GetString() == kind && item.GetProperty("id").GetString() == id);

        var weir = Find("processor", "p1");
        Assert.Equal("Weir", weir.GetProperty("name").GetString());
        Assert.True(weir.GetProperty("enabled").GetBoolean());
        Assert.True(weir.GetProperty("healthy").GetBoolean());
        Assert.Equal(2, weir.GetProperty("countToday").GetInt32());
        Assert.Equal(1, weir.GetProperty("busy").GetInt32());

        var fileFlows = Find("processor", "p2");
        Assert.False(fileFlows.GetProperty("enabled").GetBoolean());
        Assert.False(fileFlows.GetProperty("healthy").GetBoolean());
        Assert.Equal(0, fileFlows.GetProperty("countToday").GetInt32());

        Assert.Equal(2, Find("mediaServer", "ms1").GetProperty("countToday").GetInt32());
        Assert.Equal(2, Find("requests", "sr1").GetProperty("countToday").GetInt32());
        Assert.Equal("OpenSubtitles", Find("subtitles", "sp1").GetProperty("name").GetString());
        Assert.Equal(1, Find("subtitles", "sp1").GetProperty("countToday").GetInt32());

        // Lists are one tile per provider family, with how many are being watched (not a day count).
        var trakt = Find("lists", "trakt");
        Assert.Equal("Trakt", trakt.GetProperty("name").GetString());
        Assert.Equal(2, trakt.GetProperty("countToday").GetInt32());
        Assert.False(trakt.GetProperty("healthy").GetBoolean());
        var tmdb = Find("lists", "tmdb");
        Assert.Equal("TMDb", tmdb.GetProperty("name").GetString());
        Assert.Equal(1, tmdb.GetProperty("countToday").GetInt32());
        Assert.True(tmdb.GetProperty("healthy").GetBoolean());

        // Processors first, then media servers, Seerr, subtitles and lists.
        Assert.Equal(
            ["processor", "processor", "mediaServer", "requests", "subtitles", "lists", "lists"],
            connections.Select(item => item.GetProperty("kind").GetString()!).ToArray());
    }

    private static async Task ExecuteAsync(IDelunoDatabaseConnectionFactory databases, string database, string sql)
    {
        await using var connection = await databases.OpenConnectionAsync(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
