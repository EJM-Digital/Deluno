using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Settings > Tags on the whole application with its own database files: what creating, refusing, deleting and clearing
/// the unused tags leaves in the platform database itself (not only what the API says).
/// </summary>
public sealed class TagsOnDiskTests
{
    /// <summary>Reads the platform database file itself, so a test shows what really reached the disk.</summary>
    private static async Task<string?> StoredAsync(ApplicationTestHost app, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<string> CreateTagAsync(ApplicationTestHost app, string name, string color = "slate", string description = "")
    {
        var response = await app.Client.PostAsJsonAsync("/api/tags", new { name, color, description });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateIndexerAsync(ApplicationTestHost app, string name, string tags)
    {
        var response = await app.Client.PostAsJsonAsync("/api/indexers", new
        {
            name, privacy = "public", priority = 10, categories = "2000", tags, mediaScope = "both", isEnabled = false,
            protocol = "newznab", baseUrl = $"https://{name.Replace(' ', '-').ToLowerInvariant()}.example.test", apiKey = "k"
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_created_tag_is_stored_with_its_name_colour_and_note_and_a_blank_name_is_refused_without_storing_anything()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var id = await CreateTagAsync(app, "Kids", "amber", "For the little ones");

        Assert.Equal("Kids|amber|For the little ones", await StoredAsync(app, $"SELECT name || '|' || color || '|' || description FROM tags WHERE id = '{id}'"));
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/tags", new { name = "  ", color = "slate", description = "" })).StatusCode);
        Assert.Equal("1", await StoredAsync(app, "SELECT count(*) FROM tags"));
    }

    [Fact]
    public async Task A_new_tag_with_a_name_another_tag_already_has_is_refused_in_plain_words_and_stores_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateTagAsync(app, "Kids");

        var repeated = await app.Client.PostAsJsonAsync("/api/tags", new { name = "kids", color = "slate", description = "" });

        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal("1", await StoredAsync(app, "SELECT count(*) FROM tags"));
    }

    [Fact]
    public async Task Deleting_an_unused_tag_takes_its_row_out_of_the_file_and_saying_it_again_or_naming_a_tag_that_never_was_is_a_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var spare = await CreateTagAsync(app, "Spare");
        var keep = await CreateTagAsync(app, "Keep");

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/tags/{spare}")).StatusCode);

        Assert.Equal("0", await StoredAsync(app, $"SELECT count(*) FROM tags WHERE id = '{spare}'"));
        Assert.Equal("1", await StoredAsync(app, $"SELECT count(*) FROM tags WHERE id = '{keep}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/tags/{spare}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/tags/never-was")).StatusCode);
    }

    [Fact]
    public async Task A_tag_an_indexer_or_an_auto_tag_rule_still_uses_is_refused_with_who_uses_it_and_stays_in_the_file_until_they_let_go()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tag = await CreateTagAsync(app, "Anime");
        var indexerId = await CreateIndexerAsync(app, "Tagged", "Anime");

        var refused = await app.Client.DeleteAsync($"/api/tags/{tag}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()!;
        Assert.Contains("still in use by 1 indexer", message);
        Assert.Contains("Remove it from those first.", message);
        Assert.Equal("1", await StoredAsync(app, $"SELECT count(*) FROM tags WHERE id = '{tag}'"));

        // The indexer lets go; a tag only an auto-tag rule gives is held just the same.
        Assert.True((await app.Client.DeleteAsync($"/api/indexers/{indexerId}")).IsSuccessStatusCode);
        var rule = await app.Client.PostAsJsonAsync("/api/auto-tag-rules", new
        {
            name = "Anime rule", tags = new[] { "Anime" }, appliesTo = "movies", isEnabled = true,
            specifications = new[] { new { type = "genre", negate = false, value = "Animation" } }
        });
        Assert.True(rule.IsSuccessStatusCode, await rule.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.DeleteAsync($"/api/tags/{tag}")).StatusCode);
        Assert.Equal("1", await StoredAsync(app, $"SELECT count(*) FROM tags WHERE id = '{tag}'"));

        var ruleId = (await rule.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/auto-tag-rules/{ruleId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/tags/{tag}")).StatusCode);
        Assert.Equal("0", await StoredAsync(app, $"SELECT count(*) FROM tags WHERE id = '{tag}'"));
    }

    [Fact]
    public async Task Delete_unused_tags_removes_only_the_tags_nothing_uses_and_names_them_in_Activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var spareA = await CreateTagAsync(app, "Spare A");
        var spareB = await CreateTagAsync(app, "Spare B");
        var used = await CreateTagAsync(app, "Used");
        await CreateIndexerAsync(app, "Uses Used", "Used");
        Assert.Equal("3", await StoredAsync(app, "SELECT count(*) FROM tags"));

        var result = await (await app.Client.PostAsync("/api/tags/delete-unused", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, result.GetProperty("removed").GetInt32());
        Assert.Equal("1", await StoredAsync(app, "SELECT count(*) FROM tags"));
        Assert.Equal("1", await StoredAsync(app, $"SELECT count(*) FROM tags WHERE id = '{used}'"));
        Assert.Equal("0", await StoredAsync(app, $"SELECT count(*) FROM tags WHERE id IN ('{spareA}','{spareB}')"));
        var activity = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("Deleted 2 unused tags", activity);
        Assert.Contains("Spare A", activity);
        Assert.Contains("Spare B", activity);
    }
}
