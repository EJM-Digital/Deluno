using System.Net;
using System.Net.Http.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1044: indexers, download clients and notification connections take tags
/// added, removed or replaced in one action, with the title editor's modes.
/// </summary>
public sealed class BulkTagsOnConnectionsTests
{
    [Theory]
    [InlineData("indexer", "indexer_sources")]
    [InlineData("downloadClient", "download_clients")]
    [InlineData("notification", "notification_webhooks")]
    public async Task Add_keeps_the_existing_tags_and_adds_the_new_ones_on_every_selected_connection(string kind, string table)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var a = await InsertAsync(app, table, "kids,Family");
        var b = await InsertAsync(app, table, "");
        var untouched = await InsertAsync(app, table, "solo");

        var response = await PostAsync(app, kind, [a, b], ["family", "4k"], "add");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("4k,family,kids", Sorted(await ReadAsync(app, table, a)));
        Assert.Equal("4k,family", Sorted(await ReadAsync(app, table, b)));
        Assert.Equal("solo", await ReadAsync(app, table, untouched));
    }

    [Theory]
    [InlineData("indexer", "indexer_sources")]
    [InlineData("downloadClient", "download_clients")]
    [InlineData("notification", "notification_webhooks")]
    public async Task Remove_drops_only_the_named_tags(string kind, string table)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var a = await InsertAsync(app, table, "kids,family,4k");
        var b = await InsertAsync(app, table, "family");

        var response = await PostAsync(app, kind, [a, b], ["Family"], "remove");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("kids,4k", await ReadAsync(app, table, a));
        Assert.Equal("", await ReadAsync(app, table, b));
    }

    [Theory]
    [InlineData("indexer", "indexer_sources")]
    [InlineData("downloadClient", "download_clients")]
    [InlineData("notification", "notification_webhooks")]
    public async Task Replace_swaps_the_whole_tag_set(string kind, string table)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var a = await InsertAsync(app, table, "kids,family");
        var b = await InsertAsync(app, table, "");

        var response = await PostAsync(app, kind, [a, b], ["archive"], "replace");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("archive", await ReadAsync(app, table, a));
        Assert.Equal("archive", await ReadAsync(app, table, b));
    }

    [Fact]
    public async Task Replace_with_no_tags_clears_them_and_activity_says_so_in_plain_words()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var a = await InsertAsync(app, "indexer_sources", "kids", "Nyaa");

        var response = await PostAsync(app, "indexer", [a], [], "replace");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", await ReadAsync(app, "indexer_sources", a));
        var feed = await app.Client.GetStringAsync("/api/activity?pageSize=50");
        Assert.Contains("Cleared every tag from 1 indexer: Nyaa.", feed);
    }

    [Fact]
    public async Task Add_writes_one_activity_line_and_a_bad_request_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var a = await InsertAsync(app, "download_clients", "", "qBit");
        var b = await InsertAsync(app, "download_clients", "", "SAB");

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(app, "nonsense", [a], ["x"], "add")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(app, "downloadClient", [a], ["x"], "shuffle")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(app, "downloadClient", [], ["x"], "add")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(app, "downloadClient", [a], [], "add")).StatusCode);
        Assert.Equal("", await ReadAsync(app, "download_clients", a));

        await PostAsync(app, "downloadClient", [a, b], ["kids"], "add");
        var feed = await app.Client.GetStringAsync("/api/activity?pageSize=50");
        Assert.Contains("Added the tags kids to 2 download clients", feed);
    }

    private static string Sorted(string? csv) =>
        string.Join(",", (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

    private static Task<HttpResponseMessage> PostAsync(ApplicationTestHost app, string kind, string[] ids, string[] tags, string mode) =>
        app.Client.PostAsJsonAsync("/api/tags/bulk-connections", new { kind, ids, tags, mode });

    private static async Task<string> InsertAsync(ApplicationTestHost app, string table, string tags, string name = "Conn")
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        var id = Guid.NewGuid().ToString("N");
        var row = new Dictionary<string, object?> { ["id"] = id, ["name"] = name, ["tags"] = tags };
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var column = reader.GetString(1);
                var type = reader.GetString(2).ToUpperInvariant();
                if (row.ContainsKey(column)) continue;
                if (reader.GetInt32(3) == 1 && reader.IsDBNull(4))
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

    private static async Task<string?> ReadAsync(ApplicationTestHost app, string table, string id)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT tags FROM {table} WHERE id = @id;";
        SqliteRecordHelpers.AddParameter(command, "@id", id);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }
}
