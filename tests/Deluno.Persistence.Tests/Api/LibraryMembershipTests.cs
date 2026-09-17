using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Media;
using Deluno.Movies.Migrations;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #530: a title is in the libraries a person chose. Seen on the rig with three
/// movie libraries: every film joined all of them, and a proof film's own search
/// went through a different library's indexer and found nothing.
/// </summary>
public sealed class LibraryMembershipTests
{
    [Fact]
    public async Task A_film_joins_the_chosen_library_or_the_first_and_is_not_added_everywhere()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var hd = await CreateLibraryAsync(app, "A Films HD");
        var uhd = await CreateLibraryAsync(app, "B Films 4K");

        var chosen = await AddAsync(app, new { title = "Arrival", releaseYear = 2016, monitored = true, libraryId = uhd });
        var unchosen = await AddAsync(app, new { title = "Sicario", releaseYear = 2015, monitored = true });

        Assert.Equal([uhd], await LibrariesOfAsync(app, chosen));
        Assert.Equal([hd], await LibrariesOfAsync(app, unchosen));

        // Added again without a choice, it stays where it is.
        await AddAsync(app, new { title = "Arrival", releaseYear = 2016, monitored = true });
        Assert.Equal([uhd], await LibrariesOfAsync(app, chosen));

        var wrongType = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Dune", releaseYear = 2021, libraryId = "not-a-library" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
    }

    [Fact]
    public async Task A_film_can_be_kept_in_a_second_library_and_its_title_actions_then_ask_which()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var hd = await CreateLibraryAsync(app, "A Films HD");
        var uhd = await CreateLibraryAsync(app, "B Films 4K");
        var id = await AddAsync(app, new { title = "Arrival", releaseYear = 2016, monitored = true, libraryId = hd });

        var added = await app.Client.PostAsJsonAsync($"/api/movies/{id}/libraries/", new { libraryId = uhd });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal([hd, uhd], (await LibrariesOfAsync(app, id)).Order());

        // Two libraries and no choice: the search asks rather than picking one.
        using (var search = JsonDocument.Parse(await (await app.Client.PostAsync($"/api/movies/{id}/search?mode=preview", null)).Content.ReadAsStringAsync()))
        {
            Assert.Equal("choose_library", search.RootElement.GetProperty("reason").GetString());
            Assert.Contains("A Films HD", search.RootElement.GetProperty("summary").GetString(), StringComparison.Ordinal);
        }

        using (var named = JsonDocument.Parse(await (await app.Client.PostAsync($"/api/movies/{id}/search?mode=preview&libraryId={uhd}", null)).Content.ReadAsStringAsync()))
        {
            Assert.NotEqual("choose_library", named.RootElement.GetProperty("reason").GetString());
        }

        // Out of one library it holds nothing in; the last one cannot go this way.
        Assert.Equal(HttpStatusCode.OK, (await app.Client.DeleteAsync($"/api/movies/{id}/libraries/{uhd}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.DeleteAsync($"/api/movies/{id}/libraries/{hd}")).StatusCode);

        // Moved to another library.
        var moved = await app.Client.PutAsJsonAsync($"/api/movies/{id}/libraries/{hd}", new { libraryId = uhd });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal([uhd], await LibrariesOfAsync(app, id));
    }

    [Fact]
    public async Task A_library_holding_the_file_keeps_the_title()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var hd = await CreateLibraryAsync(app, "A Films HD");
        var uhd = await CreateLibraryAsync(app, "B Films 4K");
        var id = await AddAsync(app, new { title = "Arrival", releaseYear = 2016, monitored = true, libraryId = hd });
        await app.Client.PostAsJsonAsync($"/api/movies/{id}/libraries/", new { libraryId = uhd });
        await app.Services.GetRequiredService<IMediaStateRepository>().EnsureWantedStateAsync(
            MediaKind.Movie, id, hd, WantedStatuses.Covered, "Imported.", hasFile: true, "WEB 1080p", "WEB 1080p", true, CancellationToken.None);

        var remove = await app.Client.DeleteAsync($"/api/movies/{id}/libraries/{hd}");
        var move = await app.Client.PutAsJsonAsync($"/api/movies/{id}/libraries/{hd}", new { libraryId = await CreateLibraryAsync(app, "C Films Archive") });

        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, move.StatusCode);
        Assert.Contains("has a file in A Films HD", await remove.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Films_the_old_add_put_in_every_library_keep_their_copies_or_their_first_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None);
        await ExecuteAsync(connection, """
            INSERT INTO movie_entries (id, title, release_year, monitored, created_utc, updated_utc) VALUES
                ('m-held', 'Held', 2000, 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
                ('m-missing', 'Missing', 2000, 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            INSERT INTO movie_wanted_state (movie_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met, updated_utc) VALUES
                ('m-held', 'lib-1', 'missing', 'x', 0, 0, '2026-01-01T00:00:00Z'),
                ('m-held', 'lib-2', 'covered', 'x', 1, 1, '2026-01-01T00:00:00Z'),
                ('m-held', 'lib-3', 'covered', 'x', 1, 1, '2026-01-01T00:00:00Z'),
                ('m-missing', 'lib-2', 'missing', 'x', 0, 0, '2026-01-01T00:00:00Z'),
                ('m-missing', 'lib-1', 'missing', 'x', 0, 0, '2026-01-01T00:00:00Z');
            """);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new V0038MovieLibraryMembership().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT movie_id || ':' || library_id FROM movie_wanted_state WHERE movie_id IN ('m-held', 'm-missing') ORDER BY 1;";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));

        Assert.Equal(["m-held:lib-2", "m-held:lib-3", "m-missing:lib-1"], rows);
    }

    private static async Task ExecuteAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> LibrariesOfAsync(ApplicationTestHost app, string id)
    {
        var response = await app.Client.GetAsync($"/api/movies/{id}/libraries/");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(item => item.GetProperty("libraryId").GetString()!).ToArray();
    }

    private static async Task<string> AddAsync(ApplicationTestHost app, object body)
    {
        var response = await app.Client.PostAsJsonAsync("/api/movies/", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, string name)
    {
        var root = Path.Combine(app.DataRoot, name);
        Directory.CreateDirectory(root);
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name,
            mediaType = "movies",
            purpose = "collection",
            rootPath = root,
            importWorkflow = "copy",
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
