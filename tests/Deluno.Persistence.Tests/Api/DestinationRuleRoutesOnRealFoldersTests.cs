using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Settings > Final destinations on the whole application with its own databases and real folders: a rule needs a name, what it
/// matches and a destination folder that exists; a saved rule keeps its order, template and library; deleting it changes no file.
/// </summary>
public sealed class DestinationRuleRoutesOnRealFoldersTests
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

    private static object Body(string name, string kind, string value, string root, int priority = 10, bool enabled = true, string? template = null, string? libraryId = null) => new
    {
        name, mediaType = "movies", matchKind = kind, matchValue = value, rootPath = root, folderTemplate = template, priority, isEnabled = enabled, libraryId
    };

    [Fact]
    public async Task A_rule_is_saved_with_its_kind_value_folder_template_order_and_library_and_comes_back_as_saved()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Documentaries")).FullName;

        var created = await app.Client.PostAsJsonAsync("/api/destination-rules", Body("Documentaries", "genre", "Documentary", root, priority: 3, template: "{Genre}/{Title} ({Year})", libraryId: libraryId));

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var rule = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = rule.GetProperty("id").GetString()!;
        Assert.Equal("genre", rule.GetProperty("matchKind").GetString());
        Assert.Equal(root, rule.GetProperty("rootPath").GetString());
        Assert.Equal("{Genre}/{Title} ({Year})", rule.GetProperty("folderTemplate").GetString());
        Assert.Equal(3, rule.GetProperty("priority").GetInt32());
        Assert.Equal(libraryId, rule.GetProperty("libraryId").GetString());
        Assert.Equal($"Documentaries|Documentary|{root}|3|1", await StoredAsync(app, $"SELECT name || '|' || match_value || '|' || root_path || '|' || priority || '|' || is_enabled FROM destination_rules WHERE id = '{id}'"));

        var changed = await app.Client.PutAsJsonAsync($"/api/destination-rules/{id}", Body("Documentaries", "genre", "Documentary", root, priority: 3, enabled: false, template: "{Title}"));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("0", await StoredAsync(app, $"SELECT is_enabled FROM destination_rules WHERE id = '{id}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/destination-rules/no-such-rule", Body("x", "genre", "x", root))).StatusCode);
    }

    [Fact]
    public async Task A_rule_with_no_name_no_match_no_folder_or_a_folder_that_does_not_exist_is_refused_in_plain_words_and_stores_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Real")).FullName;

        var noName = await app.Client.PostAsJsonAsync("/api/destination-rules", Body("  ", "genre", "Documentary", root));
        var noMatch = await app.Client.PostAsJsonAsync("/api/destination-rules", Body("Docs", "genre", "", root));
        var noFolder = await app.Client.PostAsJsonAsync("/api/destination-rules", Body("Docs", "genre", "Documentary", ""));
        var missingFolder = await app.Client.PostAsJsonAsync("/api/destination-rules", Body("Docs", "genre", "Documentary", Path.Combine(app.DataRoot, "NotThere")));

        Assert.All(new[] { noName, noMatch, noFolder, missingFolder }, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Contains("Give this destination rule a name.", await noName.Content.ReadAsStringAsync());
        Assert.Contains("Choose what this rule should match.", await noMatch.Content.ReadAsStringAsync());
        Assert.Contains("Choose where matching titles should land.", await noFolder.Content.ReadAsStringAsync());
        Assert.Contains("rootPath", await missingFolder.Content.ReadAsStringAsync());
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM destination_rules"));
    }

    [Fact]
    public async Task Deleting_a_rule_removes_only_the_rule_and_every_film_already_placed_by_it_stays_where_it_is()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Special")).FullName;
        var film = await ApiScenes.FilmOnDiskAsync(app, "Sintel", 2010);
        var rule = (await (await app.Client.PostAsJsonAsync("/api/destination-rules", Body("Special", "title", "sintel", root))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/destination-rules/{rule}")).StatusCode);

        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM destination_rules"));
        Assert.True(File.Exists(film.FilePath));
        Assert.True(Directory.Exists(root));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/destination-rules/{rule}")).StatusCode);
    }

    [Fact]
    public async Task Nobody_who_is_not_signed_in_can_add_change_or_remove_a_rule()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Real")).FullName;
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/destination-rules", Body("x", "genre", "x", root))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/destination-rules/any")).StatusCode);
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM destination_rules"));
    }
}
