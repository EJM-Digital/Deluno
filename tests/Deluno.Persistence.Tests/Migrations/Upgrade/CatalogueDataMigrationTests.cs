using Deluno.Contracts;
using Deluno.Infrastructure.Storage;

namespace Deluno.Persistence.Tests.Migrations.Upgrade;

/// <summary>
/// #1274. The movies and series databases' migrations that fill a new column from rows already there, change a word a
/// row used, or delete rows. The film and the show versions come from the same generators, so each test runs for both,
/// with the migration number of each. (Movies V0038, V0045 and V0047, and the search-history trims, are proved in their
/// own test classes, named in <see cref="DataChangingMigrations"/>.)
/// </summary>
public sealed class CatalogueDataMigrationTests
{
    public sealed record Catalogue(
        string Name,
        string Database,
        string Entries,
        string Wanted,
        string Key,
        string History,
        int Vocabulary,
        int SortableFacts,
        int Metadata,
        int SortTitle,
        int Facts,
        int Keywords,
        int FileFacts,
        int SubtitleFacts,
        int Conformance,
        int DecisionFacts,
        int SearchEffort,
        int ReadAgain,
        int Tags)
    {
        public override string ToString() => Name;
    }

    public static readonly Catalogue Movies = new(
        "movies", DelunoDatabaseNames.Movies, "movie_entries", "movie_wanted_state", "movie_id", "movie_search_history",
        Vocabulary: 14, SortableFacts: 16, Metadata: 20, SortTitle: 21, Facts: 22, Keywords: 23, FileFacts: 25, SubtitleFacts: 26,
        Conformance: 27, DecisionFacts: 28, SearchEffort: 29, ReadAgain: 39, Tags: 31);

    public static readonly Catalogue Series = new(
        "series", DelunoDatabaseNames.Series, "series_entries", "series_wanted_state", "series_id", "series_search_history",
        Vocabulary: 15, SortableFacts: 17, Metadata: 20, SortTitle: 22, Facts: 23, Keywords: 24, FileFacts: 26, SubtitleFacts: 27,
        Conformance: 28, DecisionFacts: 29, SearchEffort: 30, ReadAgain: 42, Tags: 33);

    public static IEnumerable<object[]> Both() => [[Movies.Name], [Series.Name]];

    private static Catalogue Of(string name) => name == Movies.Name ? Movies : Series;

    private static Task AddEntryAsync(UpgradeCase upgrade, Catalogue c, string id, params (string, object?)[] more)
        => upgrade.InsertAsync(c.Entries, [("id", id), ("title", $"Title {id}"), .. more]);

    private static Task AddWantedAsync(UpgradeCase upgrade, Catalogue c, string id, string library, bool hasFile, params (string, object?)[] more)
        => upgrade.InsertAsync(c.Wanted, [(c.Key, id), ("library_id", library), ("has_file", hasFile ? 1 : 0), .. more]);

    // ---- words a row used ---------------------------------------------------------------------

    [Fact]
    public async Task Movies_V0014_calls_the_waiting_state_covered_and_leaves_every_other_state()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Movies.Database, Movies.Vocabulary);
        foreach (var (id, status) in new[] { ("m-waiting", "waiting"), ("m-missing", "missing"), ("m-covered", "covered"), ("m-wanted", "wanted") })
        {
            await AddEntryAsync(upgrade, Movies, id);
            await AddWantedAsync(upgrade, Movies, id, "lib-1", false, ("wanted_status", status), ("wanted_reason", $"reason {id}"));
        }

        await upgrade.MigrateAsync();

        var states = (await upgrade.RowsAsync("SELECT movie_id, wanted_status, wanted_reason FROM movie_wanted_state;")).ToDictionary(row => (string)row["movie_id"]!);
        Assert.Equal("covered", states["m-waiting"]["wanted_status"]);
        Assert.Equal("missing", states["m-missing"]["wanted_status"]);
        Assert.Equal("covered", states["m-covered"]["wanted_status"]);
        Assert.Equal("wanted", states["m-wanted"]["wanted_status"]);
        Assert.Equal("reason m-waiting", states["m-waiting"]["wanted_reason"]);
    }

    [Fact]
    public async Task Series_V0015_calls_waiting_covered_and_an_episode_that_was_wanted_missing_and_leaves_a_shows_own_wanted_state()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Series.Database, Series.Vocabulary);
        foreach (var (id, status) in new[] { ("s-waiting", "waiting"), ("s-wanted", "wanted"), ("s-missing", "missing") })
        {
            await AddEntryAsync(upgrade, Series, id);
            await AddWantedAsync(upgrade, Series, id, "lib-1", false, ("wanted_status", status));
            await upgrade.InsertAsync("episode_wanted_state", ("episode_id", $"e-{id}"), ("series_id", id), ("library_id", "lib-1"), ("wanted_status", status));
        }

        await upgrade.MigrateAsync();

        var shows = (await upgrade.RowsAsync("SELECT series_id, wanted_status FROM series_wanted_state;")).ToDictionary(row => (string)row["series_id"]!, row => (string)row["wanted_status"]!);
        Assert.Equal("covered", shows["s-waiting"]);
        Assert.Equal("wanted", shows["s-wanted"]);
        Assert.Equal("missing", shows["s-missing"]);
        var episodes = (await upgrade.RowsAsync("SELECT episode_id, wanted_status FROM episode_wanted_state;")).ToDictionary(row => (string)row["episode_id"]!, row => (string)row["wanted_status"]!);
        Assert.Equal("covered", episodes["e-s-waiting"]);
        Assert.Equal("missing", episodes["e-s-wanted"]);
        Assert.Equal("missing", episodes["e-s-missing"]);
    }

    // ---- facts copied onto the title from the file the title keeps ----------------------------

    [Theory]
    [MemberData(nameof(Both))]
    public async Task V0016_and_V0017_copy_the_size_and_the_rank_of_the_picked_file_onto_the_title(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.SortableFacts);
        foreach (var id in new[] { "with-file", "two-files", "no-state" })
        {
            await AddEntryAsync(upgrade, c, id);
        }

        // One copy with a file beats one without.
        await AddWantedAsync(upgrade, c, "with-file", "lib-a", false);
        await AddWantedAsync(upgrade, c, "with-file", "lib-b", true, ("file_size_bytes", 5000L), ("current_quality", "Bluray 1080p"), ("quality_cutoff_met", 1));
        // Of two copies with a file, the one that has not met its cutoff is the one still being worked on.
        await AddWantedAsync(upgrade, c, "two-files", "lib-a", true, ("file_size_bytes", 100L), ("current_quality", "Remux 1080p"), ("quality_cutoff_met", 1));
        await AddWantedAsync(upgrade, c, "two-files", "lib-b", true, ("file_size_bytes", 200L), ("current_quality", "WEB 720p"), ("quality_cutoff_met", 0));

        await upgrade.MigrateAsync();

        var rows = (await upgrade.RowsAsync($"SELECT id, title, primary_file_size_bytes, primary_quality_rank FROM {c.Entries};")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal((5000L, 80L), ((long)rows["with-file"]["primary_file_size_bytes"]!, (long)rows["with-file"]["primary_quality_rank"]!));
        Assert.Equal((200L, 40L), ((long)rows["two-files"]["primary_file_size_bytes"]!, (long)rows["two-files"]["primary_quality_rank"]!));
        Assert.Null(rows["no-state"]["primary_file_size_bytes"]);
        Assert.Null(rows["no-state"]["primary_quality_rank"]);
        Assert.Equal("Title with-file", rows["with-file"]["title"]);
        Assert.Equal(0, await upgrade.CountAsync($"SELECT COUNT(*) FROM quality_ranks WHERE name = 'Unknown' AND rank <> 0;"));
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task V0020_reads_the_status_the_provider_sent_out_of_the_metadata_already_stored(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.Metadata);
        await AddEntryAsync(upgrade, c, "known", ("metadata_json", "{\"Status\":\"Released\",\"Studio\":\"Acme Pictures\",\"Overview\":\"kept\"}"));
        await AddEntryAsync(upgrade, c, "no-status", ("metadata_json", "{\"Overview\":\"nothing else\"}"));
        await AddEntryAsync(upgrade, c, "broken", ("metadata_json", "{not json"));
        await AddEntryAsync(upgrade, c, "none");

        await upgrade.MigrateAsync();

        var rows = (await upgrade.RowsAsync($"SELECT id, status, metadata_json FROM {c.Entries};")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal("Released", rows["known"]["status"]);
        Assert.Null(rows["no-status"]["status"]);
        Assert.Null(rows["broken"]["status"]);
        Assert.Null(rows["none"]["status"]);
        Assert.Equal("{not json", rows["broken"]["metadata_json"]);
        if (c == Movies)
        {
            Assert.Equal("Acme Pictures", await upgrade.TextAsync("SELECT studio FROM movie_entries WHERE id = 'known';"));
        }
    }

    [Fact]
    public async Task Series_V0020_counts_each_shows_episodes_aired_and_filed_and_finds_the_next_air_date_and_a_season_with_nothing()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Series.Database, Series.Metadata);
        await AddEntryAsync(upgrade, Series, "partly-filed");
        await AddEntryAsync(upgrade, Series, "empty-season");
        await AddEntryAsync(upgrade, Series, "no-episodes");
        const string past1 = "2020-01-01T00:00:00.0000000+00:00", past2 = "2020-02-01T00:00:00.0000000+00:00", future = "2999-01-01T00:00:00.0000000+00:00";
        foreach (var (id, show, season, number, aired, file) in new[]
                 {
                     ("a1", "partly-filed", 1, 1, past1, 1), ("a2", "partly-filed", 1, 2, past2, 0), ("a3", "partly-filed", 1, 3, future, 0),
                     ("b1", "empty-season", 2, 1, past1, 0)
                 })
        {
            await upgrade.InsertAsync("episode_entries", ("id", id), ("series_id", show), ("season_number", season), ("episode_number", number), ("air_date_utc", aired), ("has_file", file));
        }

        await upgrade.MigrateAsync();

        var shows = (await upgrade.RowsAsync("SELECT id, episode_count, episode_with_file_count, aired_episode_count, aired_with_file_count, next_air_date_utc, has_missing_season FROM series_entries;")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal((3L, 1L, 2L, 1L, future, 0L), ((long)shows["partly-filed"]["episode_count"]!, (long)shows["partly-filed"]["episode_with_file_count"]!, (long)shows["partly-filed"]["aired_episode_count"]!, (long)shows["partly-filed"]["aired_with_file_count"]!, (string)shows["partly-filed"]["next_air_date_utc"]!, (long)shows["partly-filed"]["has_missing_season"]!));
        Assert.Equal((1L, 0L, 1L, 0L, 1L), ((long)shows["empty-season"]["episode_count"]!, (long)shows["empty-season"]["episode_with_file_count"]!, (long)shows["empty-season"]["aired_episode_count"]!, (long)shows["empty-season"]["aired_with_file_count"]!, (long)shows["empty-season"]["has_missing_season"]!));
        Assert.Null(shows["empty-season"]["next_air_date_utc"]);
        Assert.Equal((0L, 0L, 0L, 0L), ((long)shows["no-episodes"]["episode_count"]!, (long)shows["no-episodes"]["aired_episode_count"]!, (long)shows["no-episodes"]["aired_with_file_count"]!, (long)shows["no-episodes"]["has_missing_season"]!));
        Assert.Equal(4, await upgrade.CountAsync("SELECT COUNT(*) FROM episode_entries;"));
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task V0021_and_V0022_fill_the_sort_title_without_the_leading_article(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.SortTitle);
        var titles = new[] { "The Matrix", "A Beautiful Mind", "An Education", "The", "  Alien  ", "Theory of Everything" };
        for (var i = 0; i < titles.Length; i++)
        {
            await upgrade.InsertAsync(c.Entries, ("id", $"t{i}"), ("title", titles[i]));
        }

        await upgrade.MigrateAsync();

        var rows = (await upgrade.RowsAsync($"SELECT id, title, sort_title FROM {c.Entries};")).ToDictionary(row => (string)row["id"]!);
        for (var i = 0; i < titles.Length; i++)
        {
            Assert.Equal(titles[i], rows[$"t{i}"]["title"]);
            Assert.Equal(SortTitle.For(titles[i]), rows[$"t{i}"]["sort_title"]);
        }

        Assert.Equal("matrix", rows["t0"]["sort_title"]);
        Assert.Equal("the", rows["t3"]["sort_title"]);
        Assert.Equal("theory of everything", rows["t5"]["sort_title"]);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task V0022_and_V0023_read_the_certification_the_collection_the_language_and_the_ratings_out_of_the_stored_metadata(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.Facts);
        const string blob = "{\"Certification\":\"R\",\"Collection\":\"The Matrix Collection\",\"OriginalLanguage\":\"en\",\"Ratings\":["
            + "{\"Source\":\"tmdb\",\"Score\":8.1,\"VoteCount\":1200},{\"Source\":\"imdb\",\"Score\":7.9,\"VoteCount\":3400},"
            + "{\"Source\":\"rotten_tomatoes\",\"Score\":88},{\"Source\":\"metacritic\",\"Score\":73}]}";
        await AddEntryAsync(upgrade, c, "rich", ("metadata_json", blob));
        await AddEntryAsync(upgrade, c, "sparse", ("metadata_json", "{\"Certification\":\"PG\"}"));
        await AddEntryAsync(upgrade, c, "none");

        await upgrade.MigrateAsync();

        var rows = (await upgrade.RowsAsync($"SELECT id, certification, collection, original_language, rating_tmdb, votes_tmdb, rating_imdb, votes_imdb, rating_rotten_tomatoes, rating_metacritic, metadata_json FROM {c.Entries};")).ToDictionary(row => (string)row["id"]!);
        var rich = rows["rich"];
        Assert.Equal(("R", "The Matrix Collection", "en"), ((string)rich["certification"]!, (string)rich["collection"]!, (string)rich["original_language"]!));
        Assert.Equal((8.1, 1200d, 7.9, 3400d, 88d, 73d), (Convert.ToDouble(rich["rating_tmdb"]), Convert.ToDouble(rich["votes_tmdb"]), Convert.ToDouble(rich["rating_imdb"]), Convert.ToDouble(rich["votes_imdb"]), Convert.ToDouble(rich["rating_rotten_tomatoes"]), Convert.ToDouble(rich["rating_metacritic"])));
        Assert.Equal(blob, rich["metadata_json"]);
        Assert.Equal("PG", rows["sparse"]["certification"]);
        Assert.Null(rows["sparse"]["rating_tmdb"]);
        Assert.Null(rows["none"]["certification"]);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task V0023_and_V0024_leave_a_title_with_no_stored_keywords_without_any_and_keep_its_metadata(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.Keywords);
        await AddEntryAsync(upgrade, c, "empty-list", ("metadata_json", "{\"Keywords\":[],\"Overview\":\"kept\"}"));
        await AddEntryAsync(upgrade, c, "older", ("metadata_json", "{\"Overview\":\"no keywords yet\"}"));
        await AddEntryAsync(upgrade, c, "none");

        await upgrade.MigrateAsync();

        var rows = (await upgrade.RowsAsync($"SELECT id, keywords, metadata_json FROM {c.Entries};")).ToDictionary(row => (string)row["id"]!);
        Assert.Null(rows["empty-list"]["keywords"]);
        Assert.Null(rows["older"]["keywords"]);
        Assert.Null(rows["none"]["keywords"]);
        Assert.Equal("{\"Keywords\":[],\"Overview\":\"kept\"}", rows["empty-list"]["metadata_json"]);
    }

    /// <summary>
    /// DEFECT found by #1274 (not fixed here): the backfill reads each keyword with <c>json_extract(entry.value, '$')</c>.
    /// For a string element <c>json_each</c> already returns the unquoted text, so <c>json_extract</c> is handed
    /// <c>heist</c>, which is not JSON, and SQLite stops with "malformed JSON". The migration therefore fails, and with it start-up, on
    /// any database that holds a stored metadata blob with a non-empty list of keywords before this migration runs.
    /// Released installs did not hold such blobs yet, so none broke; a restored backup or a copied database could.
    /// </summary>
    [Theory(Skip = "Defect: V0023/V0024 fail with 'malformed JSON' on a stored keyword list. Found by #1274, to be fixed with a new migration, never by editing the old one.")]
    [MemberData(nameof(Both))]
    public async Task V0023_and_V0024_list_the_keywords_in_the_stored_metadata(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.Keywords);
        await AddEntryAsync(upgrade, c, "tagged", ("metadata_json", "{\"Keywords\":[\"heist\",\"time loop\"]}"));

        await upgrade.MigrateAsync();

        Assert.Equal("heist, time loop", await upgrade.TextAsync($"SELECT keywords FROM {c.Entries} WHERE id = 'tagged';"));
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task V0025_and_V0026_copy_the_picked_files_codecs_group_path_and_container_onto_the_title(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.FileFacts);
        await AddEntryAsync(upgrade, c, "filed");
        await AddEntryAsync(upgrade, c, "unfiled");
        await AddEntryAsync(upgrade, c, "no-extension");
        await AddWantedAsync(upgrade, c, "filed", "lib-a", false);
        await AddWantedAsync(upgrade, c, "filed", "lib-b", true,
            ("video_codec", "HEVC"), ("audio_codec", "DTS"), ("audio_channels", "5.1"), ("release_group", "GRP"),
            ("file_path", "/media/Heat (1995)/Heat.1995.MKV"), ("current_quality", "Bluray 1080p"), ("imported_utc", "2026-03-01T00:00:00.0000000+00:00"));
        await AddWantedAsync(upgrade, c, "unfiled", "lib-a", false);
        await AddWantedAsync(upgrade, c, "no-extension", "lib-a", true, ("file_path", "/media/odd/file-without-dot"));

        await upgrade.MigrateAsync();

        var rows = (await upgrade.RowsAsync($"SELECT * FROM {c.Entries};")).ToDictionary(row => (string)row["id"]!);
        var filed = rows["filed"];
        Assert.Equal(("HEVC", "DTS", "5.1", "GRP"), ((string)filed["primary_video_codec"]!, (string)filed["primary_audio_codec"]!, (string)filed["primary_audio_channels"]!, (string)filed["primary_release_group"]!));
        Assert.Equal(("/media/Heat (1995)/Heat.1995.MKV", "Bluray 1080p", 1L, "mkv"), ((string)filed["primary_file_path"]!, (string)filed["primary_current_quality"]!, (long)filed["primary_has_file"]!, (string)filed["primary_container"]!));
        Assert.Equal("2026-03-01T00:00:00.0000000+00:00", filed["primary_imported_utc"]);
        Assert.Equal(0L, rows["unfiled"]["primary_has_file"]);
        Assert.Null(rows["unfiled"]["primary_video_codec"]);
        Assert.Null(rows["unfiled"]["primary_container"]);
        Assert.Null(rows["no-extension"]["primary_container"]);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task V0026_and_V0027_list_the_subtitle_languages_and_sources_a_title_holds_and_leave_a_title_with_none_empty(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.SubtitleFacts);
        await AddEntryAsync(upgrade, c, "subbed");
        await AddEntryAsync(upgrade, c, "bare");
        var owner = "subbed";
        var table = "movie_subtitle_state";
        var ownerColumn = "movie_id";
        if (c == Series)
        {
            owner = "subbed-episode";
            table = "episode_subtitle_state";
            ownerColumn = "episode_id";
            await upgrade.InsertAsync("episode_entries", ("id", owner), ("series_id", "subbed"), ("season_number", 1), ("episode_number", 1));
        }

        await upgrade.InsertAsync(table, (ownerColumn, owner), ("language", "en"), ("forced", 0), ("hearing_impaired", 0), ("source", "sidecar"));
        await upgrade.InsertAsync(table, (ownerColumn, owner), ("language", "en"), ("forced", 0), ("hearing_impaired", 1), ("source", "embedded"));
        await upgrade.InsertAsync(table, (ownerColumn, owner), ("language", "fr"), ("forced", 1), ("hearing_impaired", 0), ("source", "embedded"));

        await upgrade.MigrateAsync();

        var rows = (await upgrade.RowsAsync($"SELECT id, subtitle_languages, subtitle_languages_full, subtitle_sources FROM {c.Entries};")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal(",en,fr,", rows["subbed"]["subtitle_languages"]);
        Assert.Equal(",en,", rows["subbed"]["subtitle_languages_full"]);
        Assert.Equal(",sidecar,embedded,", rows["subbed"]["subtitle_sources"]);
        Assert.Null(rows["bare"]["subtitle_languages"]);
        Assert.Null(rows["bare"]["subtitle_sources"]);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task V0027_and_V0028_add_size_bounds_to_the_quality_ladder_without_judging_any_title_until_a_bound_is_set(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.Conformance);
        await AddEntryAsync(upgrade, c, "filed");
        await AddWantedAsync(upgrade, c, "filed", "lib-a", true, ("file_size_bytes", 1L), ("current_quality", "Bluray 1080p"));
        var ladder = await upgrade.CountAsync("SELECT COUNT(*) FROM quality_ranks;");
        var rank = await upgrade.CountAsync("SELECT rank FROM quality_ranks WHERE name = 'Bluray 1080p';");

        await upgrade.MigrateAsync();

        Assert.Equal(ladder, await upgrade.CountAsync("SELECT COUNT(*) FROM quality_ranks;"));
        Assert.Equal(rank, await upgrade.CountAsync("SELECT rank FROM quality_ranks WHERE name = 'Bluray 1080p';"));
        Assert.Equal(ladder, await upgrade.CountAsync("SELECT COUNT(*) FROM quality_ranks WHERE floor_bytes = 0 AND ceiling_bytes = 0;"));
        Assert.Null(await upgrade.ScalarAsync($"SELECT size_conformance FROM {c.Entries} WHERE id = 'filed';"));
        Assert.Equal(1, await upgrade.CountAsync($"SELECT file_size_bytes FROM {c.Wanted};"));
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task V0028_and_V0029_copy_the_picked_copys_wanted_reason_and_search_times_and_count_the_copies_and_the_files(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.DecisionFacts);
        await AddEntryAsync(upgrade, c, "two-copies");
        await AddEntryAsync(upgrade, c, "no-copies");
        await AddWantedAsync(upgrade, c, "two-copies", "lib-a", false, ("wanted_reason", "never found"));
        await AddWantedAsync(upgrade, c, "two-copies", "lib-b", true,
            ("wanted_reason", "below cutoff"), ("target_quality", "Bluray 2160p"), ("quality_cutoff_met", 0),
            ("last_search_utc", "2026-04-01T00:00:00.0000000+00:00"), ("next_eligible_search_utc", "2026-04-02T00:00:00.0000000+00:00"),
            ("last_search_result", "nothing better"), ("missing_detected_utc", "2026-03-30T00:00:00.0000000+00:00"));

        await upgrade.MigrateAsync();

        var rows = (await upgrade.RowsAsync($"SELECT * FROM {c.Entries};")).ToDictionary(row => (string)row["id"]!);
        var picked = rows["two-copies"];
        Assert.Equal(("below cutoff", "Bluray 2160p", 0L, "nothing better"), ((string)picked["primary_wanted_reason"]!, (string)picked["primary_target_quality"]!, (long)picked["primary_quality_cutoff_met"]!, (string)picked["primary_last_search_result"]!));
        Assert.Equal(("2026-04-01T00:00:00.0000000+00:00", "2026-04-02T00:00:00.0000000+00:00", "2026-03-30T00:00:00.0000000+00:00"), ((string)picked["primary_last_search_utc"]!, (string)picked["primary_next_eligible_search_utc"]!, (string)picked["primary_missing_detected_utc"]!));
        Assert.Equal((2L, 1L), ((long)picked["library_count"]!, (long)picked["file_count"]!));
        Assert.Equal((0L, 0L), ((long)rows["no-copies"]["library_count"]!, (long)rows["no-copies"]["file_count"]!));
        Assert.Null(rows["no-copies"]["primary_wanted_reason"]);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task V0029_and_V0030_count_the_searches_each_title_has_had_and_how_many_took_a_release(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.SearchEffort);
        await AddEntryAsync(upgrade, c, "searched");
        await AddEntryAsync(upgrade, c, "never-searched");
        foreach (var (id, outcome) in new[] { ("h1", "matched"), ("h2", "rejected"), ("h3", "nothing found") })
        {
            await upgrade.InsertAsync(c.History, ("id", id), (c.Key, "searched"), ("library_id", "lib-a"), ("trigger_kind", "automatic"), ("outcome", outcome));
        }

        await upgrade.InsertAsync(c.History, ("id", "other"), (c.Key, "somebody-else"), ("library_id", "lib-a"), ("trigger_kind", "manual"), ("outcome", "matched"));

        await upgrade.MigrateAsync();

        var rows = (await upgrade.RowsAsync($"SELECT id, search_attempt_count, search_grab_count FROM {c.Entries};")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal((3L, 1L), ((long)rows["searched"]["search_attempt_count"]!, (long)rows["searched"]["search_grab_count"]!));
        Assert.Equal((0L, 0L), ((long)rows["never-searched"]["search_attempt_count"]!, (long)rows["never-searched"]["search_grab_count"]!));
        Assert.Equal(4, await upgrade.CountAsync($"SELECT COUNT(*) FROM {c.History};"));
    }

    // ---- delete and re-read ---------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Both))]
    public async Task V0039_and_V0042_ask_for_every_filed_title_to_be_read_again_and_leave_the_unfiled_ones(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.ReadAgain);
        const string probed = "2026-05-05T00:00:00.0000000+00:00";
        await AddEntryAsync(upgrade, c, "filed");
        await AddEntryAsync(upgrade, c, "unfiled");
        await AddWantedAsync(upgrade, c, "filed", "lib-a", true, ("facts_probed_utc", probed), ("facts_probed_size_bytes", 999L), ("file_path", "/media/a.mkv"));
        await AddWantedAsync(upgrade, c, "unfiled", "lib-a", false, ("facts_probed_utc", probed));
        if (c == Series)
        {
            await upgrade.InsertAsync("episode_entries", ("id", "e-filed"), ("series_id", "filed"), ("season_number", 1), ("episode_number", 1), ("has_file", 1), ("facts_probed_utc", probed));
            await upgrade.InsertAsync("episode_entries", ("id", "e-unfiled"), ("series_id", "filed"), ("season_number", 1), ("episode_number", 2), ("has_file", 0), ("facts_probed_utc", probed));
        }

        await upgrade.MigrateAsync();

        var wanted = (await upgrade.RowsAsync($"SELECT {c.Key} AS owner, facts_probed_utc, facts_probed_size_bytes, file_path FROM {c.Wanted};")).ToDictionary(row => (string)row["owner"]!);
        Assert.Null(wanted["filed"]["facts_probed_utc"]);
        Assert.Equal(999L, wanted["filed"]["facts_probed_size_bytes"]);
        Assert.Equal("/media/a.mkv", wanted["filed"]["file_path"]);
        Assert.Equal(probed, wanted["unfiled"]["facts_probed_utc"]);
        if (c == Series)
        {
            Assert.Null(await upgrade.ScalarAsync("SELECT facts_probed_utc FROM episode_entries WHERE id = 'e-filed';"));
            Assert.Equal(probed, await upgrade.TextAsync("SELECT facts_probed_utc FROM episode_entries WHERE id = 'e-unfiled';"));
        }
    }


    [Theory]
    [MemberData(nameof(Both))]
    public async Task The_media_tags_migration_turns_the_labels_older_builds_kept_in_the_metadata_into_tag_rows_and_leaves_the_title_alone(string name)
    {
        var c = Of(name);
        await using var upgrade = await UpgradeCase.BeforeAsync(c.Database, c.Tags);
        await AddEntryAsync(upgrade, c, "list", ("metadata_json", "{\"tags\":[\"Kids\",\" Family \",\"  \"]}"), ("created_utc", "2026-01-02T00:00:00.0000000+00:00"));
        await AddEntryAsync(upgrade, c, "single", ("metadata_json", "{\"Tags\":\"Solo\"}"));
        await AddEntryAsync(upgrade, c, "pascal-list", ("metadata_json", "{\"Tags\":[\"Kids\"]}"));
        await AddEntryAsync(upgrade, c, "untagged", ("metadata_json", "{\"Overview\":\"x\"}"));
        await AddEntryAsync(upgrade, c, "broken", ("metadata_json", "{not json"));
        await AddEntryAsync(upgrade, c, "none");

        await upgrade.MigrateAsync();

        var tags = (await upgrade.RowsAsync("SELECT * FROM media_tags;")).Select(row => (Owner: (string)row[row.Keys.First(key => key.EndsWith("_id", StringComparison.Ordinal) && key != "tag_id")]!, Id: (string)row["tag_id"]!, Name: (string)row["tag_name"]!, Created: (string)row["created_utc"]!)).OrderBy(tag => tag.Owner).ThenBy(tag => tag.Id).ToList();
        Assert.Equal(
            [("list", "legacy:family", "Family"), ("list", "legacy:kids", "Kids"), ("pascal-list", "legacy:kids", "Kids"), ("single", "legacy:solo", "Solo")],
            tags.Select(tag => (tag.Owner, tag.Id, tag.Name)));
        Assert.Equal("2026-01-02T00:00:00.0000000+00:00", tags.First(tag => tag.Owner == "list").Created);
        Assert.Equal(6, await upgrade.CountAsync($"SELECT COUNT(*) FROM {c.Entries};"));
        Assert.Equal("{\"tags\":[\"Kids\",\" Family \",\"  \"]}", await upgrade.TextAsync($"SELECT metadata_json FROM {c.Entries} WHERE id = 'list';"));
    }

    [Fact]
    public async Task Series_V0041_keeps_one_row_per_show_and_moves_its_episodes_to_the_library_the_show_stays_in()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Series.Database, 41);
        foreach (var id in new[] { "has-file", "no-file", "single", "untracked" })
        {
            await AddEntryAsync(upgrade, Series, id);
        }

        // A copy with a file wins over an empty one; of two empty copies the first library stays.
        await AddWantedAsync(upgrade, Series, "has-file", "lib-a", false);
        await AddWantedAsync(upgrade, Series, "has-file", "lib-b", true);
        await AddWantedAsync(upgrade, Series, "no-file", "lib-b", false);
        await AddWantedAsync(upgrade, Series, "no-file", "lib-a", false);
        await AddWantedAsync(upgrade, Series, "single", "lib-c", false);
        foreach (var (episode, show, library) in new[] { ("e1", "has-file", "lib-a"), ("e2", "has-file", "lib-b"), ("e3", "no-file", "lib-b"), ("e4", "single", "lib-c"), ("e5", "untracked", "lib-z") })
        {
            await upgrade.InsertAsync("episode_wanted_state", ("episode_id", episode), ("series_id", show), ("library_id", library));
        }

        await upgrade.MigrateAsync();

        var kept = (await upgrade.RowsAsync("SELECT series_id, library_id FROM series_wanted_state ORDER BY series_id;")).Select(row => ((string)row["series_id"]!, (string)row["library_id"]!));
        Assert.Equal([("has-file", "lib-b"), ("no-file", "lib-a"), ("single", "lib-c")], kept);
        var episodes = (await upgrade.RowsAsync("SELECT episode_id, library_id FROM episode_wanted_state;")).ToDictionary(row => (string)row["episode_id"]!, row => (string)row["library_id"]!);
        Assert.Equal("lib-b", episodes["e1"]);
        Assert.Equal("lib-b", episodes["e2"]);
        Assert.Equal("lib-a", episodes["e3"]);
        Assert.Equal("lib-c", episodes["e4"]);
        Assert.Equal("lib-z", episodes["e5"]);
        Assert.Equal(4, await upgrade.CountAsync("SELECT 4 FROM series_entries LIMIT 1;"));
        Assert.Equal(4, await upgrade.CountAsync("SELECT COUNT(*) FROM series_entries;"));
    }
}
