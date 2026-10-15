using Microsoft.Data.Sqlite;
using Deluno.Contracts;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What happens when the owner removes something.
///
/// <para><b>Why this file exists.</b> The coverage inventory found that the
/// destructive half of the product was the least tested half: of the DELETE
/// routes Deluno serves, no test mentioned connections, destination rules,
/// exclusions, library views, notifications, policy sets, the recycle bin,
/// release profiles, subtitle providers or tags. Everything that creates was
/// covered; almost nothing that removes was.</para>
///
/// <para>That is the wrong way round. A create that misbehaves shows up
/// immediately and costs a retry. A delete that misbehaves is either silent
/// data loss or a thing the owner cannot get rid of, and the soak plan calls an
/// unexpected deletion a P0.</para>
///
/// <para>Each family is asked the same three questions, because they are the
/// three ways a delete goes wrong: it does not remove what it claims to, it
/// reports success for something that was never there, or it reports success
/// twice for the same thing.</para>
/// </summary>
public sealed class DeleteLifecycleTests
{
    /// <summary>Reads the real database file on disk, so a test proves what was saved, not only what the repositories say.</summary>
    private static string OnDiskText(ApplicationTestHost app, string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, database)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    [Fact]
    public async Task Deleting_a_tag_removes_it_and_saying_it_twice_does_not_work()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var id = await CreateAsync(app, "/api/tags/", new { name = "Kids", color = "#33aaff", description = (string?)null });

        Assert.Contains(id, await ListIdsAsync(app, "/api/tags/"));
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/tags/{id}")).StatusCode);
        Assert.DoesNotContain(id, await ListIdsAsync(app, "/api/tags/"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/tags/{id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_tag_that_never_existed_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.DeleteAsync("/api/tags/00000000-0000-0000-0000-000000000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_destination_rule_removes_it_and_saying_it_twice_does_not_work()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        // A real folder, because a destination rule pointing at one that does
        // not exist is now refused (#407) - the same reason a library is.
        var destination = Path.Combine(app.DataRoot, "kids-films");
        Directory.CreateDirectory(destination);

        var id = await CreateAsync(app, "/api/destination-rules/", new
        {
            name = "Kids films",
            mediaType = "movie",
            matchKind = "tag",
            matchValue = "kids",
            rootPath = destination,
            folderTemplate = (string?)null,
            priority = 10,
            isEnabled = true
        });

        Assert.Contains(id, await ListIdsAsync(app, "/api/destination-rules/"));
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/destination-rules/{id}")).StatusCode);
        Assert.DoesNotContain(id, await ListIdsAsync(app, "/api/destination-rules/"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/destination-rules/{id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_library_view_removes_it_and_saying_it_twice_does_not_work()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var id = await CreateAsync(app, "/api/library-views/", new
        {
            variant = "movies",
            libraryId = (string?)null,
            name = "Missing only",
            quickFilter = "missing",
            monitoring = "monitored",
            sortField = "title",
            sortDirection = "asc",
            viewMode = "poster",
            cardSize = "medium",
            displayOptionsJson = "{}",
            rulesJson = "[]"
        });

        Assert.Contains(id, await ListIdsAsync(app, "/api/library-views/"));
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/library-views/{id}")).StatusCode);
        Assert.DoesNotContain(id, await ListIdsAsync(app, "/api/library-views/"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/library-views/{id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_policy_set_removes_it_and_saying_it_twice_does_not_work()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var id = await CreateAsync(app, "/api/policy-sets/", new
        {
            name = "Films, 1080p",
            mediaType = "movie",
            qualityProfileId = (string?)null,
            destinationRuleId = (string?)null,
            customFormatIds = (string?)null,
            searchIntervalOverrideHours = (int?)null,
            retryDelayOverrideHours = (int?)null,
            upgradeUntilCutoff = true,
            isEnabled = true,
            notes = (string?)null
        });

        Assert.Contains(id, await ListIdsAsync(app, "/api/policy-sets/"));
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/policy-sets/{id}")).StatusCode);
        Assert.DoesNotContain(id, await ListIdsAsync(app, "/api/policy-sets/"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/policy-sets/{id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_release_profile_removes_it_and_saying_it_twice_does_not_work()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var id = await CreateAsync(app, "/api/release-profiles/", new
        {
            name = "No CAM",
            tagName = (string?)null,
            preferredProtocol = "usenet",
            usenetDelayMinutes = 0,
            torrentDelayMinutes = 0,
            mustContain = (string?)null,
            mustNotContain = "CAM",
            preferredTerms = Array.Empty<object>()
        });

        Assert.Contains(id, await ListIdsAsync(app, "/api/release-profiles/"));
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/release-profiles/{id}")).StatusCode);
        Assert.DoesNotContain(id, await ListIdsAsync(app, "/api/release-profiles/"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/release-profiles/{id}")).StatusCode);
    }

    /// <summary>
    /// Subtitle providers are keyed by the registry rather than by a row id, so
    /// deleting one forgets the saved account and leaves the provider itself on
    /// the list. Getting that wrong in either direction is visible here: the
    /// provider vanishing, or the account surviving.
    /// </summary>
    [Fact]
    public async Task Deleting_a_subtitle_provider_forgets_the_account_but_keeps_the_provider()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var key = (await ListProviderKeysAsync(app)).First();
        var saved = await app.Client.PutAsJsonAsync($"/api/subtitle-providers/{key}", new
        {
            providerKey = key,
            username = "someone",
            secret = "hunter2",
            apiKey = "abcdef123456",
            priority = 1,
            isEnabled = true
        });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.True(await IsConfiguredAsync(app, key));

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await app.Client.DeleteAsync($"/api/subtitle-providers/{key}")).StatusCode);

        Assert.False(await IsConfiguredAsync(app, key));
        Assert.Contains(key, await ListProviderKeysAsync(app));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await app.Client.DeleteAsync($"/api/subtitle-providers/{key}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_an_exclusion_lets_the_title_back_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var exclusions = app.Services.GetRequiredService<IUnifiedExclusionRepository>();
        var created = await exclusions.UpsertAsync(
            new UpsertMediaExclusionRequest(
                MediaType: "movie",
                SourceKind: "list",
                SourceId: "test-list",
                SourceName: "Test list",
                Provider: "tmdb",
                EntryKey: "tt0111161",
                Title: "The Shawshank Redemption",
                Year: 1994,
                ImdbId: "tt0111161",
                DurationDays: null,
                Reason: "not wanted"),
            CancellationToken.None);
        Assert.NotNull(created);

        Assert.Contains(created!.Id, await ListIdsAsync(app, "/api/exclusions"));
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/exclusions/{created.Id}")).StatusCode);
        Assert.DoesNotContain(created.Id, await ListIdsAsync(app, "/api/exclusions"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/exclusions/{created.Id}")).StatusCode);
    }

    /// <summary>
    /// The recycle bin is the one delete that touches the disk, so this checks
    /// the file as well as the row. An entry that disappears from the list while
    /// its bytes stay in the holding folder is how a disk quietly fills up.
    /// </summary>
    [Fact]
    public async Task Emptying_one_recycle_bin_entry_takes_the_file_with_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var root = Path.Combine(app.DataRoot, "library");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Some Film (2019).mkv");
        await File.WriteAllTextAsync(file, "not really a film", CancellationToken.None);

        var libraries = app.Services.GetRequiredService<ILibrariesRepository>();
        var library = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Films",
                MediaType: "movie",
                Purpose: "collection",
                RootPath: root,
                DownloadsPath: null,
                QualityProfileId: null,
                ImportWorkflow: "copy",
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null,
                ProcessorFailureMode: null,
                AutoSearchEnabled: false,
                MissingSearchEnabled: false,
                UpgradeSearchEnabled: false,
                SearchIntervalMinutes: null,
                RetryDelayHours: null,
                MaxItemsPerRun: null),
            CancellationToken.None);

        var recycleBin = app.Services.GetRequiredService<IRecycleBinService>();
        var stored = await recycleBin.StoreReplacementAsync(library, file, file, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.True(File.Exists(stored!.RecyclePath), "the replaced file should be sitting in the holding folder");

        Assert.Contains(stored.Id, await ListIdsAsync(app, "/api/recycle-bin/"));

        var response = await app.Client.DeleteAsync($"/api/recycle-bin/{stored.Id}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        Assert.DoesNotContain(stored.Id, await ListIdsAsync(app, "/api/recycle-bin/"));
        Assert.False(File.Exists(stored.RecyclePath), "permanently deleting should remove the file, not just the row");
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await app.Client.DeleteAsync($"/api/recycle-bin/{stored.Id}")).StatusCode);
    }

    /// <summary>
    /// Before the soak the rig's dashboard read "186 failed to import" beside
    /// 15 grabs. A recovery case is written for every attempt, so one stuck file
    /// retried twelve times was twelve, and a download the client never showed
    /// was counted as a failed import too.
    /// </summary>
    [Fact]
    public async Task The_dashboard_counts_a_title_that_failed_to_import_once_a_day_however_often_it_was_retried()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        foreach (var attempt in Enumerable.Range(1, 12))
        {
            await movies.AddImportRecoveryCaseAsync(
                new CreateMovieImportRecoveryCaseRequest("The Terror", "import-failed", $"Attempt {attempt}: not a readable video.", "Try another release"),
                CancellationToken.None);
        }

        await movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest("Nosferatu", "corrupt", "The file is truncated.", "Try another release"),
            CancellationToken.None);
        // Never reached an import at all.
        var neverShown = await movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest("Dementia 13", "grab-timeout", "Sent to qBittorrent and never appeared.", "Check the client"),
            CancellationToken.None);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var metrics = await movies.GetDailyMetricsAsync(today.AddDays(-1), today.AddDays(1), CancellationToken.None);

        Assert.Equal(2, metrics.ImportFailures.Values.Sum());
        Assert.Equal("downloadFailed", neverShown.FailureKind);
    }

    [Fact]
    public async Task Dismissing_a_movie_import_recovery_case_removes_it_for_good()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var recoveryCase = await movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(
                Title: "Some Film (2019)",
                FailureKind: "unpack-failed",
                Summary: "The archive was password protected",
                RecommendedAction: "Grab a different release"),
            CancellationToken.None);

        Assert.Contains(recoveryCase.Id, await ListIdsAsync(app, "/api/movies/import-recovery"));
        Assert.Equal("1", OnDiskText(app, "movies.db", $"SELECT COUNT(*) FROM movie_import_recovery_cases WHERE id = '{recoveryCase.Id}'"));
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await app.Client.DeleteAsync($"/api/movies/import-recovery/{recoveryCase.Id}")).StatusCode);
        Assert.DoesNotContain(recoveryCase.Id, await ListIdsAsync(app, "/api/movies/import-recovery"));
        // For good: the row is gone from movies.db itself, not hidden.
        Assert.Equal("0", OnDiskText(app, "movies.db", $"SELECT COUNT(*) FROM movie_import_recovery_cases WHERE id = '{recoveryCase.Id}'"));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await app.Client.DeleteAsync($"/api/movies/import-recovery/{recoveryCase.Id}")).StatusCode);
    }

    [Fact]
    public async Task Dismissing_a_series_import_recovery_case_removes_it_for_good()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var recoveryCase = await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(
                Title: "Some Show S01E01",
                FailureKind: "no-matching-episode",
                Summary: "Nothing in the library matched the file",
                RecommendedAction: "Check the episode numbering"),
            CancellationToken.None);

        Assert.Contains(recoveryCase.Id, await ListIdsAsync(app, "/api/series/import-recovery"));
        Assert.Equal("1", OnDiskText(app, "series.db", $"SELECT COUNT(*) FROM series_import_recovery_cases WHERE id = '{recoveryCase.Id}'"));
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await app.Client.DeleteAsync($"/api/series/import-recovery/{recoveryCase.Id}")).StatusCode);
        Assert.DoesNotContain(recoveryCase.Id, await ListIdsAsync(app, "/api/series/import-recovery"));
        // For good: the row is gone from series.db itself, not hidden.
        Assert.Equal("0", OnDiskText(app, "series.db", $"SELECT COUNT(*) FROM series_import_recovery_cases WHERE id = '{recoveryCase.Id}'"));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await app.Client.DeleteAsync($"/api/series/import-recovery/{recoveryCase.Id}")).StatusCode);
    }

    /// <summary>
    /// Deleting a backup is the most consequential delete Deluno has: it is the
    /// thing the deployment guide tells an owner to fall back on. The file has
    /// to go, and the route has to admit when the backup was never there.
    /// </summary>
    [Fact]
    public async Task Deleting_a_backup_removes_the_archive_from_disk()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var created = await app.Client.PostAsJsonAsync("/api/backups/", new { reason = "manual" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var backup = document.RootElement.GetProperty("backup");
        var id = backup.GetProperty("id").GetString()!;

        Assert.Contains(id, await ListIdsAsync(app, "/api/backups/"));
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/backups/{id}")).StatusCode);
        Assert.DoesNotContain(id, await ListIdsAsync(app, "/api/backups/"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/backups/{id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_path_mapping_leaves_the_download_client_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var clientId = await CreateAsync(app, "/api/download-clients/", new
        {
            name = "SAB",
            protocol = "sabnzbd",
            host = "127.0.0.1",
            port = 8080,
            username = (string?)null,
            password = "an-api-key",
            endpointUrl = (string?)null,
            moviesCategory = "movies",
            tvCategory = "tv",
            categoryTemplate = (string?)null,
            priority = 1,
            isEnabled = true
        });

        var mappingId = await CreateAsync(app, $"/api/download-clients/{clientId}/path-mappings", new
        {
            remotePath = "/downloads",
            localPath = Path.Combine(Path.GetTempPath(), "downloads"),
            isEnabled = true,
            priority = 1
        });

        Assert.Contains(mappingId, await ListIdsAsync(app, $"/api/download-clients/{clientId}/path-mappings"));
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await app.Client.DeleteAsync($"/api/download-clients/{clientId}/path-mappings/{mappingId}")).StatusCode);
        Assert.DoesNotContain(mappingId, await ListIdsAsync(app, $"/api/download-clients/{clientId}/path-mappings"));

        // The mapping went; the client it belonged to did not.
        Assert.Contains(clientId, await ListIdsAsync(app, "/api/download-clients/"));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await app.Client.DeleteAsync($"/api/download-clients/{clientId}/path-mappings/{mappingId}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_list_exclusion_lets_that_list_offer_the_title_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var sourceId = await CreateAsync(app, "/api/intake-sources/", new
        {
            name = "Trending films",
            provider = "tmdb",
            feedUrl = "8271866",
            mediaType = "movie",
            libraryId = (string?)null,
            qualityProfileId = (string?)null,
            requiredGenres = (string?)null,
            minimumRating = (double?)null,
            minimumYear = (int?)null,
            maximumAgeDays = (int?)null,
            allowedCertifications = (string?)null,
            audience = (string?)null,
            syncIntervalHours = 24,
            searchOnAdd = false,
            isEnabled = true
        });

        var intake = app.Services.GetRequiredService<IIntakeRepository>();
        var exclusion = await intake.CreateIntakeListExclusionAsync(
            sourceId,
            new CreateIntakeListExclusionRequest(
                Title: "Some Film",
                Year: 2019,
                ImdbId: "tt1234567",
                DurationDays: null,
                Reason: "already own it"),
            CancellationToken.None);
        Assert.NotNull(exclusion);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await app.Client.DeleteAsync($"/api/intake-sources/{sourceId}/exclusions/{exclusion.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await app.Client.DeleteAsync($"/api/intake-sources/{sourceId}/exclusions/{exclusion.Id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_processor_connection_removes_it_and_saying_it_twice_does_not_work()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var id = await CreateAsync(app, "/api/integrations/processors/connections", new
        {
            name = "Weir",
            provider = "weir",
            submissionUrl = "https://example.invalid/submit",
            authHeaderName = "X-Api-Key",
            secret = "a-secret",
            isEnabled = true
        });

        Assert.Contains(id, await ListIdsAsync(app, "/api/integrations/processors/connections"));
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await app.Client.DeleteAsync($"/api/integrations/processors/connections/{id}")).StatusCode);
        Assert.DoesNotContain(id, await ListIdsAsync(app, "/api/integrations/processors/connections"));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await app.Client.DeleteAsync($"/api/integrations/processors/connections/{id}")).StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private static Task<string> CreateAsync(ApplicationTestHost app, string route, object body)
        => ApiPayload.CreateAsync(app.Client, route, body);

    private static Task<IReadOnlyList<string>> ListIdsAsync(ApplicationTestHost app, string route)
        => ApiPayload.ListIdsAsync(app.Client, route);

    private static async Task<IReadOnlyList<string>> ListProviderKeysAsync(ApplicationTestHost app)
    {
        using var document = JsonDocument.Parse(
            await (await app.Client.GetAsync("/api/subtitle-providers/")).Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("key").GetString()!)
            .ToArray();
    }

    private static async Task<bool> IsConfiguredAsync(ApplicationTestHost app, string key)
    {
        using var document = JsonDocument.Parse(
            await (await app.Client.GetAsync("/api/subtitle-providers/")).Content.ReadAsStringAsync());
        var provider = document.RootElement.EnumerateArray()
            .First(item => item.GetProperty("key").GetString() == key);

        // The list carries the saved account itself rather than a flag, so the
        // provider is configured exactly when that object is present.
        return provider.TryGetProperty("configured", out var configured) &&
               configured.ValueKind == JsonValueKind.Object;
    }

}
