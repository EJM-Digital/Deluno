using Deluno.Infrastructure.Storage;

namespace Deluno.Persistence.Tests.Migrations.Upgrade;

/// <summary>
/// #1274. The platform database's migrations that rewrite, move or delete data. Each test builds the database as the
/// migration before it left it, puts in the rows an install of that time held, applies the migration, and checks what
/// every row means afterwards. (The migrations that freeze a setting for an existing install, such as V0073 and V0096, have
/// their own test classes, named in <see cref="DataChangingMigrations"/>.)
/// </summary>
public sealed class PlatformDataMigrationTests
{
    private const string Db = DelunoDatabaseNames.Platform;

    [Fact]
    public async Task V0002_gives_every_existing_user_its_own_security_stamp_and_keeps_the_user()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 2);
        await upgrade.InsertAsync("users", ("id", "u1"), ("username", "alice"), ("display_name", "Alice"), ("password_hash", "hash-1"));
        await upgrade.InsertAsync("users", ("id", "u2"), ("username", "bob"), ("display_name", "Bob"), ("password_hash", "hash-2"));

        await upgrade.MigrateAsync();

        var users = await upgrade.RowsAsync("SELECT id, username, display_name, password_hash, security_stamp FROM users ORDER BY id;");
        Assert.Equal(["alice", "bob"], users.Select(user => (string)user["username"]!));
        Assert.Equal(["Alice", "Bob"], users.Select(user => (string)user["display_name"]!));
        Assert.Equal(["hash-1", "hash-2"], users.Select(user => (string)user["password_hash"]!));
        Assert.All(users, user => Assert.Matches("^[0-9a-f]{32}$", (string)user["security_stamp"]!));
        Assert.NotEqual(users[0]["security_stamp"], users[1]["security_stamp"]);
    }

    [Theory]
    [InlineData("indexer_sources")]
    [InlineData("download_clients")]
    public async Task V0003_moves_the_old_health_words_to_the_new_ones_and_a_switched_off_connection_reads_disabled(string table)
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 3);
        var rows = new (string Id, string Status, int Enabled, string Expected)[]
        {
            ("ready-on", "ready", 1, "untested"),
            ("paused-on", "paused", 1, "disabled"),
            ("attention-on", "attention", 1, "degraded"),
            ("healthy-on", "healthy", 1, "healthy"),
            ("down-on", "down", 1, "down"),
            ("healthy-off", "healthy", 0, "disabled"),
            ("ready-off", "ready", 0, "disabled")
        };
        foreach (var row in rows)
        {
            await upgrade.InsertAsync(table, ("id", row.Id), ("name", row.Id), ("is_enabled", row.Enabled), ("health_status", row.Status));
        }

        await upgrade.MigrateAsync();

        var after = (await upgrade.RowsAsync($"SELECT id, name, health_status, last_health_test_utc, last_health_latency_ms, last_health_failure_category FROM {table};"))
            .ToDictionary(row => (string)row["id"]!);
        foreach (var row in rows)
        {
            Assert.Equal(row.Expected, after[row.Id]["health_status"]);
            Assert.Equal(row.Id, after[row.Id]["name"]);
            Assert.Null(after[row.Id]["last_health_test_utc"]);
            Assert.Null(after[row.Id]["last_health_latency_ms"]);
            Assert.Null(after[row.Id]["last_health_failure_category"]);
        }
    }

    [Fact]
    public async Task V0013_removes_the_four_untouched_demo_profiles_but_keeps_one_a_library_uses()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 13);
        const string stamp = "2026-01-01T00:00:00.0000000+00:00";
        foreach (var (id, name, type) in new[] { ("demo-1", "Movies / Standard", "movies"), ("demo-2", "Movies / Premium 4K", "movies"), ("demo-3", "TV Shows / Standard", "tv"), ("demo-4", "TV Shows / Premium 4K", "tv") })
        {
            await upgrade.InsertAsync("quality_profiles", ("id", id), ("name", name), ("media_type", type), ("created_utc", stamp), ("updated_utc", stamp));
        }

        await upgrade.InsertAsync("quality_profiles", ("id", "mine"), ("name", "My profile"), ("media_type", "movies"), ("created_utc", stamp), ("updated_utc", stamp));
        await upgrade.InsertAsync("libraries", ("id", "lib-1"), ("name", "Movies"), ("quality_profile_id", "demo-2"));

        await upgrade.MigrateAsync();

        var kept = (await upgrade.RowsAsync("SELECT id FROM quality_profiles ORDER BY id;")).Select(row => (string)row["id"]!);
        Assert.Equal(["demo-2", "mine"], kept);
        Assert.Equal("demo-2", await upgrade.TextAsync("SELECT quality_profile_id FROM libraries WHERE id = 'lib-1';"));
    }

    [Fact]
    public async Task V0013_keeps_a_profile_the_person_edited_and_a_group_of_demo_names_that_is_not_all_four()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 13);
        const string made = "2026-01-01T00:00:00.0000000+00:00";
        await upgrade.InsertAsync("quality_profiles", ("id", "edited"), ("name", "Movies / Standard"), ("media_type", "movies"), ("created_utc", made), ("updated_utc", "2026-02-01T00:00:00.0000000+00:00"));
        await upgrade.InsertAsync("quality_profiles", ("id", "g1"), ("name", "Movies / Premium 4K"), ("media_type", "movies"), ("created_utc", "2026-03-01T00:00:00.0000000+00:00"), ("updated_utc", "2026-03-01T00:00:00.0000000+00:00"));
        await upgrade.InsertAsync("quality_profiles", ("id", "g2"), ("name", "TV Shows / Standard"), ("media_type", "tv"), ("created_utc", "2026-03-01T00:00:00.0000000+00:00"), ("updated_utc", "2026-03-01T00:00:00.0000000+00:00"));
        await upgrade.InsertAsync("quality_profiles", ("id", "g3"), ("name", "TV Shows / Premium 4K"), ("media_type", "tv"), ("created_utc", "2026-03-01T00:00:00.0000000+00:00"), ("updated_utc", "2026-03-01T00:00:00.0000000+00:00"));
        await upgrade.InsertAsync("quality_profiles", ("id", "preset"), ("name", "Movies / Standard"), ("media_type", "tv"), ("created_utc", made), ("updated_utc", made), ("preset_id", "movies-standard"));

        await upgrade.MigrateAsync();

        Assert.Equal(["edited", "g1", "g2", "g3", "preset"], (await upgrade.RowsAsync("SELECT id FROM quality_profiles ORDER BY id;")).Select(row => (string)row["id"]!));
    }

    [Fact]
    public async Task V0021_renames_old_quality_tier_names_in_the_allowed_list_and_the_cutoff_and_drops_the_duplicate_it_makes()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 21);
        await upgrade.InsertAsync("quality_profiles", ("id", "old"), ("name", "Old names"), ("media_type", "movies"),
            ("cutoff_quality", "webdl-1080p"),
            ("allowed_qualities", "webdl-1080p, Bluray 4K, WEB 1080p, My own tier"));
        await upgrade.InsertAsync("quality_profiles", ("id", "clean"), ("name", "Clean"), ("media_type", "movies"),
            ("cutoff_quality", "Remux 2160p"),
            ("allowed_qualities", "WEB 1080p, Remux 2160p"));
        await upgrade.InsertAsync("quality_profiles", ("id", "empty"), ("name", "Empty"), ("media_type", "tv"),
            ("cutoff_quality", "HDTV 4K"),
            ("allowed_qualities", ""));

        await upgrade.MigrateAsync();

        var profiles = (await upgrade.RowsAsync("SELECT id, cutoff_quality, allowed_qualities FROM quality_profiles;")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal("WEB 1080p, Bluray 2160p, My own tier", profiles["old"]["allowed_qualities"]);
        Assert.Equal("WEB 1080p", profiles["old"]["cutoff_quality"]);
        Assert.Equal("WEB 1080p, Remux 2160p", profiles["clean"]["allowed_qualities"]);
        Assert.Equal("Remux 2160p", profiles["clean"]["cutoff_quality"]);
        Assert.Equal(string.Empty, profiles["empty"]["allowed_qualities"]);
        Assert.Equal("HDTV 2160p", profiles["empty"]["cutoff_quality"]);
    }

    [Fact]
    public async Task V0026_moves_a_saved_monitored_filter_to_the_monitoring_axis_and_leaves_other_views_alone()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 26);
        foreach (var (id, filter) in new[] { ("v-monitored", "monitored"), ("v-unmonitored", "unmonitored"), ("v-missing", "missing"), ("v-all", "all") })
        {
            await upgrade.InsertAsync("library_views", ("id", id), ("name", id), ("quick_filter", filter));
        }

        await upgrade.MigrateAsync();

        var views = (await upgrade.RowsAsync("SELECT id, name, quick_filter, monitoring FROM library_views;")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal(("all", "monitored"), ((string)views["v-monitored"]["quick_filter"]!, (string)views["v-monitored"]["monitoring"]!));
        Assert.Equal(("all", "unmonitored"), ((string)views["v-unmonitored"]["quick_filter"]!, (string)views["v-unmonitored"]["monitoring"]!));
        Assert.Equal("missing", views["v-missing"]["quick_filter"]);
        Assert.Null(views["v-missing"]["monitoring"]);
        Assert.Equal("all", views["v-all"]["quick_filter"]);
        Assert.Null(views["v-all"]["monitoring"]);
        Assert.Equal("v-monitored", views["v-monitored"]["name"]);
    }

    [Fact]
    public async Task V0033_copies_every_import_list_exclusion_into_the_unified_list_with_its_source_and_keeps_the_originals()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 33);
        await upgrade.InsertAsync("intake_sources", ("id", "src-tv"), ("name", "My TV list"), ("provider", "trakt"), ("media_type", "tv"));
        await upgrade.InsertAsync("intake_sources", ("id", "src-film"), ("name", "My film list"), ("provider", "tmdb"), ("media_type", "movies"));
        await upgrade.InsertAsync("intake_source_exclusions", ("id", "e1"), ("source_id", "src-tv"), ("entry_key", "tt1"), ("title", "A Show"), ("year", 2020), ("imdb_id", "tt1"), ("expires_utc", "2027-01-01T00:00:00.0000000+00:00"), ("created_utc", "2026-01-01T00:00:00.0000000+00:00"), ("updated_utc", "2026-01-02T00:00:00.0000000+00:00"));
        await upgrade.InsertAsync("intake_source_exclusions", ("id", "e2"), ("source_id", "src-film"), ("entry_key", "tt2"), ("title", "A Film"), ("reason", "Seen it"));
        await upgrade.InsertAsync("intake_source_exclusions", ("id", "e3"), ("source_id", "gone"), ("entry_key", "tt3"), ("title", "Orphan"));
        var reasonE1 = await upgrade.TextAsync("SELECT reason FROM intake_source_exclusions WHERE id = 'e1';");

        await upgrade.MigrateAsync();

        var rows = (await upgrade.RowsAsync("SELECT * FROM media_exclusions;")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal(3, rows.Count);
        Assert.All(rows.Values, row => Assert.Equal("import-list", row["source_kind"]));
        Assert.Equal(("tv", "src-tv", "My TV list", "trakt"), ((string)rows["e1"]["media_type"]!, (string)rows["e1"]["source_id"]!, (string)rows["e1"]["source_name"]!, (string)rows["e1"]["provider"]!));
        Assert.Equal("A Show", rows["e1"]["title"]);
        Assert.Equal(2020L, rows["e1"]["year"]);
        Assert.Equal("tt1", rows["e1"]["imdb_id"]);
        Assert.Equal(reasonE1, rows["e1"]["reason"]);
        Assert.Equal("2027-01-01T00:00:00.0000000+00:00", rows["e1"]["expires_utc"]);
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", rows["e1"]["created_utc"]);
        Assert.Equal("2026-01-02T00:00:00.0000000+00:00", rows["e1"]["updated_utc"]);
        Assert.Equal(("movies", "Seen it", "tmdb"), ((string)rows["e2"]["media_type"]!, (string)rows["e2"]["reason"]!, (string)rows["e2"]["provider"]!));
        Assert.Equal(("movies", string.Empty, "unknown"), ((string)rows["e3"]["media_type"]!, (string)rows["e3"]["source_name"]!, (string)rows["e3"]["provider"]!));
        Assert.Equal(3, await upgrade.CountAsync("SELECT COUNT(*) FROM intake_source_exclusions;"));
    }

    [Fact]
    public async Task V0050_drops_the_playback_goal_tables_and_nothing_else()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 50);
        await upgrade.InsertAsync("playback_device_profiles", ("id", "p1"));
        await upgrade.InsertAsync("playback_device_groups", ("id", "g1"));
        await upgrade.InsertAsync("playback_goals", ("id", "goal-1"));
        await upgrade.InsertAsync("users", ("id", "u1"), ("username", "alice"));
        await upgrade.InsertAsync("quality_profiles", ("id", "q1"), ("name", "Mine"), ("media_type", "movies"));

        await upgrade.MigrateAsync();

        Assert.Equal(0, await upgrade.CountAsync("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'playback\\_%' ESCAPE '\\';"));
        Assert.Equal("alice", await upgrade.TextAsync("SELECT username FROM users WHERE id = 'u1';"));
        Assert.Equal("Mine", await upgrade.TextAsync("SELECT name FROM quality_profiles WHERE id = 'q1';"));
    }

    [Fact]
    public async Task V0057_moves_processor_timeouts_into_the_allowed_range_and_the_old_default_to_two_days()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 57);
        var minutes = new (string Id, int? Before, long? After)[]
        {
            ("old-default", 360, 2880), ("short", 120, 1440), ("floor", 1440, 1440), ("middle", 5000, 5000),
            ("ceiling", 10080, 10080), ("huge", 20000, 10080)
        };
        foreach (var row in minutes)
        {
            await upgrade.InsertAsync("libraries", ("id", row.Id), ("name", row.Id), ("processor_timeout_minutes", row.Before));
        }

        await upgrade.InsertAsync("processor_handoffs", ("id", "h1"), ("status", "waiting"), ("release_name", "A.Release"));

        await upgrade.MigrateAsync();

        var after = (await upgrade.RowsAsync("SELECT id, processor_timeout_minutes FROM libraries;")).ToDictionary(row => (string)row["id"]!, row => row["processor_timeout_minutes"]);
        foreach (var row in minutes)
        {
            Assert.Equal(row.After, after[row.Id]);
        }

        var handoff = (await upgrade.RowsAsync("SELECT status, release_name, processor_state, processor_message FROM processor_handoffs;")).Single();
        Assert.Equal(("waiting", "A.Release"), ((string)handoff["status"]!, (string)handoff["release_name"]!));
        Assert.Null(handoff["processor_state"]);
    }

    [Fact]
    public async Task V0066_forgets_the_old_strike_records_and_keeps_every_other_setting()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 66);
        await upgrade.InsertAsync("system_settings", ("setting_key", "download-health.records.v1"), ("setting_value", "[{\"old\":true}]"));
        await upgrade.InsertAsync("system_settings", ("setting_key", "download-health.records.v2"), ("setting_value", "[{\"new\":true}]"));
        await upgrade.InsertAsync("system_settings", ("setting_key", "media.renameOnImport"), ("setting_value", "true"));

        await upgrade.MigrateAsync();

        var settings = (await upgrade.RowsAsync("SELECT setting_key, setting_value FROM system_settings;")).ToDictionary(row => (string)row["setting_key"]!, row => (string)row["setting_value"]!);
        Assert.False(settings.ContainsKey("download-health.records.v1"));
        Assert.Equal("[{\"new\":true}]", settings["download-health.records.v2"]);
        Assert.Equal("true", settings["media.renameOnImport"]);
    }

    [Fact]
    public async Task V0093_drops_the_global_remove_completed_setting_and_keeps_every_other_setting()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 93);
        await upgrade.InsertAsync("system_settings", ("setting_key", "media.removeCompletedDownloads"), ("setting_value", "true"));
        await upgrade.InsertAsync("system_settings", ("setting_key", "media.renameOnImport"), ("setting_value", "false"));

        await upgrade.MigrateAsync();

        var settings = (await upgrade.RowsAsync("SELECT setting_key, setting_value FROM system_settings;")).ToDictionary(row => (string)row["setting_key"]!, row => (string)row["setting_value"]!);
        Assert.Equal(["media.renameOnImport"], settings.Keys);
        Assert.Equal("false", settings["media.renameOnImport"]);
    }

    [Fact]
    public async Task V0109_stamps_each_path_mapping_with_its_clients_host_in_lower_case_and_leaves_hostless_ones_empty()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 109);
        await upgrade.InsertAsync("download_clients", ("id", "c-host"), ("name", "qBit"), ("protocol", "torrent"), ("host", "  NAS.Local "));
        await upgrade.InsertAsync("download_clients", ("id", "c-blank"), ("name", "SAB"), ("protocol", "usenet"), ("host", "  "));
        await upgrade.InsertAsync("download_clients", ("id", "c-null"), ("name", "NZBGet"), ("protocol", "usenet"));
        foreach (var client in new[] { "c-host", "c-blank", "c-null", "c-removed" })
        {
            await upgrade.InsertAsync("download_client_path_mappings", ("id", $"m-{client}"), ("download_client_id", client), ("remote_path", "/downloads"), ("local_path", "D:\\Downloads"));
        }

        await upgrade.MigrateAsync();

        var hosts = (await upgrade.RowsAsync("SELECT id, host, remote_path, local_path FROM download_client_path_mappings;")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal("nas.local", hosts["m-c-host"]["host"]);
        Assert.Null(hosts["m-c-blank"]["host"]);
        Assert.Null(hosts["m-c-null"]["host"]);
        Assert.Null(hosts["m-c-removed"]["host"]);
        Assert.All(hosts.Values, row => Assert.Equal("/downloads", row["remote_path"]));
        Assert.All(hosts.Values, row => Assert.Equal("D:\\Downloads", row["local_path"]));
    }

    [Fact]
    public async Task V0121_turns_each_Seerr_4K_library_mapping_into_a_routing_rule_and_keeps_the_rules_already_there()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 121);
        await upgrade.InsertAsync("libraries", ("id", "lib-4k-films"), ("name", "4K Films"), ("root_path", "/media/4k-films"));
        await upgrade.InsertAsync("libraries", ("id", "lib-4k-tv"), ("name", "4K TV"), ("root_path", "/media/4k-tv"));
        await upgrade.InsertAsync("seerr_servers", ("id", "s1"), ("name", "Home Seerr"), ("base_url", "http://seerr"), ("movie_4k_library_id", "lib-4k-films"), ("series_4k_library_id", "lib-4k-tv"));
        await upgrade.InsertAsync("seerr_servers", ("id", "s2"), ("name", "Dangling"), ("base_url", "http://other"), ("movie_4k_library_id", "no-such-library"), ("series_4k_library_id", "   "));
        await upgrade.InsertAsync("seerr_servers", ("id", "s3"), ("name", "No 4K"), ("base_url", "http://third"));
        await upgrade.InsertAsync("destination_rules", ("id", "existing"), ("name", "By hand"), ("media_type", "movies"), ("match_kind", "genre"), ("match_value", "Anime"), ("root_path", "/media/anime"), ("priority", 5));

        await upgrade.MigrateAsync();

        var rules = await upgrade.RowsAsync("SELECT id, name, media_type, match_kind, match_value, root_path, priority, is_enabled, library_id FROM destination_rules ORDER BY media_type, name;");
        Assert.Equal(3, rules.Count);
        var existing = rules.Single(rule => (string)rule["id"]! == "existing");
        Assert.Equal(("By hand", "genre", "Anime", "/media/anime", 5L), ((string)existing["name"]!, (string)existing["match_kind"]!, (string)existing["match_value"]!, (string)existing["root_path"]!, (long)existing["priority"]!));
        Assert.Null(existing["library_id"]);

        var films = rules.Single(rule => (string?)rule["library_id"] == "lib-4k-films");
        Assert.Equal(("movies", "requested4k", "true", "/media/4k-films", 10L, 1L), ((string)films["media_type"]!, (string)films["match_kind"]!, (string)films["match_value"]!, (string)films["root_path"]!, (long)films["priority"]!, (long)films["is_enabled"]!));
        Assert.StartsWith("Home Seerr 4K", (string)films["name"]!);
        var tv = rules.Single(rule => (string?)rule["library_id"] == "lib-4k-tv");
        Assert.Equal(("tv", "requested4k", "/media/4k-tv"), ((string)tv["media_type"]!, (string)tv["match_kind"]!, (string)tv["root_path"]!));

        // The connection's own columns stay, so a rollback or a re-read still sees them.
        Assert.Equal("lib-4k-films", await upgrade.TextAsync("SELECT movie_4k_library_id FROM seerr_servers WHERE id = 's1';"));
    }

    [Fact]
    public async Task V0125_copies_the_tag_name_into_the_tag_set_and_numbers_the_profiles_tagged_first_then_by_name_then_by_id()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 125);
        foreach (var (id, tag) in new[] { ("p1", " Zeta "), ("p2", "alpha"), ("p3", ""), ("p4", "Beta") })
        {
            await upgrade.InsertAsync("release_profiles", ("id", id), ("name", $"Profile {id}"), ("tag_name", tag));
        }

        await upgrade.MigrateAsync();

        var profiles = (await upgrade.RowsAsync("SELECT id, name, tag_name, tag_names, position FROM release_profiles;")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal(" Zeta ", profiles["p1"]["tag_name"]);
        Assert.Equal(["Zeta", "alpha", string.Empty, "Beta"], new[] { "p1", "p2", "p3", "p4" }.Select(id => (string)profiles[id]["tag_names"]!));
        Assert.Equal(new long[] { 2, 0, 3, 1 }, new[] { "p1", "p2", "p3", "p4" }.Select(id => (long)profiles[id]["position"]!));
        Assert.Equal("Profile p1", profiles["p1"]["name"]);
    }
}
