using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1039: a tag name is stored on indexers, download clients, notification
/// connections, acquisition rules, seeding rules, library source rules and
/// destination rules, besides the titles that carry it. Renaming or deleting
/// the tag must reach every one of them.
/// </summary>
public sealed class RenamingATagUpdatesEverythingThatUsesItTests
{
    private static readonly string[] Stores =
    [
        "indexer_sources", "download_clients", "notification_webhooks", "sharing_rules",
        "library_source_links", "release_profiles", "destination_rules"
    ];

    [Fact]
    public async Task Renaming_a_tag_changes_it_in_every_store_and_the_usage_lists_each_user()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (tagId, ids) = await SeedAsync(app, "kids");

        using var usage = JsonDocument.Parse(await app.Client.GetStringAsync("/api/tags/usage"));
        var entry = usage.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetString() == tagId);
        var kinds = entry.GetProperty("users").EnumerateArray().Select(user => user.GetProperty("kind").GetString()).ToHashSet();
        Assert.Superset(
            new HashSet<string?> { "indexer", "downloadClient", "notification", "sharingRule", "librarySource", "acquisitionRule", "destinationRule" },
            kinds);
        Assert.All(entry.GetProperty("users").EnumerateArray(), user => Assert.False(string.IsNullOrWhiteSpace(user.GetProperty("link").GetString())));
        Assert.False(entry.GetProperty("isUnused").GetBoolean());

        var response = await app.Client.PutAsJsonAsync($"/api/tags/{tagId}", new { name = "Family", color = "slate", description = "" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("teens,Family", await ReadAsync(app, "indexer_sources", "tags", ids["indexer_sources"]));
        Assert.Equal("Family", await ReadAsync(app, "download_clients", "tags", ids["download_clients"]));
        Assert.Equal("Family", await ReadAsync(app, "notification_webhooks", "tags", ids["notification_webhooks"]));
        Assert.Equal("Family", await ReadAsync(app, "sharing_rules", "tags_csv", ids["sharing_rules"]));
        Assert.Equal("Family", await ReadAsync(app, "library_source_links", "required_tags", ids["library_source_links"]));
        Assert.Equal("Family", await ReadAsync(app, "library_source_links", "excluded_tags", ids["library_source_links"]));
        Assert.Equal("Family", await ReadAsync(app, "release_profiles", "tag_name", ids["release_profiles"]));
        Assert.Equal("Family", await ReadAsync(app, "release_profiles", "tag_names", ids["release_profiles"]));
        Assert.Equal("Family", await ReadAsync(app, "destination_rules", "match_value", ids["destination_rules"]));
        var titleTags = await app.Services.GetRequiredService<IMediaTagStore>().ListAsync(MediaKind.Movie, ids["movie"], CancellationToken.None);
        Assert.Equal("Family", Assert.Single(titleTags).Name);
    }


    /// <summary>
    /// #1341: a name another tag already has (in any letter case) is refused with the reason, as a rename is, instead of the
    /// database's unique index turning it into a bare server error the screen can only call "could not be created".
    /// </summary>
    [Fact]
    public async Task Creating_a_tag_with_a_name_another_tag_has_is_refused_with_the_reason_in_any_letter_case()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var first = await app.Client.PostAsJsonAsync("/api/tags", new { name = "Kids", color = "slate", description = "" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        foreach (var repeated in new[] { "Kids", "KIDS", "  kids  " })
        {
            var response = await app.Client.PostAsJsonAsync("/api/tags", new { name = repeated, color = "slate", description = "" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Another tag already has that name.", body.RootElement.GetProperty("errors").GetProperty("name")[0].GetString());
        }

        using var tags = JsonDocument.Parse(await app.Client.GetStringAsync("/api/tags"));
        Assert.Single(tags.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Every_title_is_listed_with_its_tags_and_monitored_flag_untagged_ones_included()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var store = app.Services.GetRequiredService<IMediaTagStore>();
        var tagged = await InsertAsync(app, "movie_entries", new() { ["title"] = "Frozen", ["monitored"] = "1" }, DelunoDatabaseNames.Movies);
        var plain = await InsertAsync(app, "movie_entries", new() { ["title"] = "Sintel", ["monitored"] = "0" }, DelunoDatabaseNames.Movies);
        await store.ReplaceAsync(
            MediaKind.Movie,
            tagged,
            [new MediaTagAssignment("t1", "kidz"), new MediaTagAssignment("t2", "family")],
            CancellationToken.None);

        var titles = await store.ListAllTitleTagsAsync(MediaKind.Movie, CancellationToken.None);

        var frozen = Assert.Single(titles, item => item.Id == tagged);
        Assert.True(frozen.IsMonitored);
        Assert.Equal(["family", "kidz"], frozen.Tags.Order().ToArray());
        var sintel = Assert.Single(titles, item => item.Id == plain);
        Assert.False(sintel.IsMonitored);
        Assert.Empty(sintel.Tags);
    }

    [Fact]
    public async Task A_tag_in_use_cannot_be_deleted_until_nothing_uses_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (tagId, ids) = await SeedAsync(app, "kids");

        var refused = await app.Client.DeleteAsync($"/api/tags/{tagId}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()!;
        Assert.Contains("1 title", message);
        Assert.Contains("1 indexer", message);
        Assert.Contains("1 download client", message);
        Assert.Contains("1 notification", message);
        Assert.Contains("1 seeding rule", message);
        Assert.Contains("1 library source rule", message);
        Assert.Contains("1 release rule", message);
        Assert.Contains("1 destination rule", message);

        // Clear one kind of user at a time: it stays refused until the last is gone.
        var steps = new[]
        {
            "UPDATE indexer_sources SET tags = ''",
            "UPDATE download_clients SET tags = ''",
            "UPDATE notification_webhooks SET tags = ''",
            "UPDATE sharing_rules SET tags_csv = NULL",
            "UPDATE library_source_links SET required_tags = '', excluded_tags = ''",
            "DELETE FROM release_profiles",
            "DELETE FROM destination_rules"
        };
        foreach (var sql in steps)
        {
            Assert.Equal(HttpStatusCode.Conflict, (await app.Client.DeleteAsync($"/api/tags/{tagId}")).StatusCode);
            await ExecuteAsync(app, sql);
        }

        // Only the title is left.
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.DeleteAsync($"/api/tags/{tagId}")).StatusCode);
        await app.Services.GetRequiredService<IMediaTagStore>().ReplaceAsync(MediaKind.Movie, ids["movie"], [], CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/tags/{tagId}")).StatusCode);
        Assert.DoesNotContain(tagId, await ApiPayload.ListIdsAsync(app.Client, "/api/tags/"));
    }

    private static async Task ExecuteAsync(ApplicationTestHost app, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = sql + ";";
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Unused_tags_are_listed_as_unused_and_only_removed_when_asked()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (usedId, _) = await SeedAsync(app, "kids");
        var unusedResponse = await app.Client.PostAsJsonAsync("/api/tags/", new { name = "Spare", color = "slate", description = "" });
        var unusedId = (await unusedResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        using var usage = JsonDocument.Parse(await app.Client.GetStringAsync("/api/tags/usage"));
        var byId = usage.RootElement.EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!);
        Assert.True(byId[unusedId].GetProperty("isUnused").GetBoolean());
        Assert.False(byId[usedId].GetProperty("isUnused").GetBoolean());

        // Nothing removes it on its own.
        Assert.Contains(unusedId, await ApiPayload.ListIdsAsync(app.Client, "/api/tags/"));

        var removed = await app.Client.PostAsync("/api/tags/delete-unused", null);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var ids = await ApiPayload.ListIdsAsync(app.Client, "/api/tags/");
        Assert.DoesNotContain(unusedId, ids);
        Assert.Contains(usedId, ids);
    }

    [Fact]
    public async Task Renaming_a_tag_to_a_name_another_tag_has_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (tagId, _) = await SeedAsync(app, "kids");
        await app.Client.PostAsJsonAsync("/api/tags/", new { name = "Family", color = "slate", description = "" });

        var response = await app.Client.PutAsJsonAsync($"/api/tags/{tagId}", new { name = "family", color = "slate", description = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<(string TagId, Dictionary<string, string> Ids)> SeedAsync(ApplicationTestHost app, string name)
    {
        var created = await app.Client.PostAsJsonAsync("/api/tags/", new { name, color = "slate", description = "" });
        var tagId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var ids = new Dictionary<string, string>();

        // Libraries and indexers first: source links reference both.
        var libraryId = await InsertAsync(app, "libraries", []);
        ids["indexer_sources"] = await InsertAsync(app, "indexer_sources", new() { ["tags"] = "teens," + name });
        ids["download_clients"] = await InsertAsync(app, "download_clients", new() { ["tags"] = name });
        ids["notification_webhooks"] = await InsertAsync(app, "notification_webhooks", new() { ["tags"] = name });
        ids["sharing_rules"] = await InsertAsync(app, "sharing_rules", new() { ["tags_csv"] = name, ["category_pattern"] = null, ["tracker_pattern"] = null });
        ids["library_source_links"] = await InsertAsync(app, "library_source_links", new()
        {
            ["library_id"] = libraryId,
            ["indexer_id"] = ids["indexer_sources"],
            ["required_tags"] = name,
            ["excluded_tags"] = name
        });
        ids["release_profiles"] = await InsertAsync(app, "release_profiles", new() { ["tag_name"] = name });
        ids["destination_rules"] = await InsertAsync(app, "destination_rules", new() { ["match_kind"] = "tag", ["match_value"] = name });

        var movieId = await InsertAsync(app, "movie_entries", [], DelunoDatabaseNames.Movies);
        ids["movie"] = movieId;
        await app.Services.GetRequiredService<IMediaTagStore>()
            .ReplaceAsync(MediaKind.Movie, movieId, [new MediaTagAssignment(tagId, name)], CancellationToken.None);
        return (tagId, ids);
    }

    /// <summary>
    /// Inserts one row, filling every required column that has no default with
    /// a placeholder so the test names only the columns it cares about.
    /// </summary>
    private static async Task<string> InsertAsync(ApplicationTestHost app, string table, Dictionary<string, string?> values, string database = DelunoDatabaseNames.Platform)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(database, CancellationToken.None);
        var id = Guid.NewGuid().ToString("N");
        var row = new Dictionary<string, object?> { ["id"] = id };
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var column = reader.GetString(1);
                var type = reader.GetString(2).ToUpperInvariant();
                var notNull = reader.GetInt32(3) == 1;
                var hasDefault = !reader.IsDBNull(4);
                if (values.ContainsKey(column))
                {
                    row[column] = values[column];
                }
                else if (notNull && !hasDefault && column != "id")
                {
                    row[column] = type.Contains("INT") ? 1 : type.Contains("REAL") ? 1.0 : column.EndsWith("_utc", StringComparison.Ordinal) ? DateTimeOffset.UtcNow.ToString("O") : "x";
                }
            }
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = $"INSERT INTO {table} ({string.Join(", ", row.Keys)}) VALUES ({string.Join(", ", row.Keys.Select(key => "@" + key))});";
        foreach (var (key, value) in row)
        {
            SqliteRecordHelpers.AddParameter(insert, "@" + key, value);
        }

        await insert.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<string?> ReadAsync(ApplicationTestHost app, string table, string column, string id)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE id = @id;";
        SqliteRecordHelpers.AddParameter(command, "@id", id);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }
}
