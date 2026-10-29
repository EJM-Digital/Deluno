using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Retention;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Two retention lists a person reads (coverage register, area api, low rows): the titles a rule could take and which it already holds,
/// and the Leaving soon collection each media server keeps.
/// </summary>
public sealed class RetentionListsReadByAPersonLowRowsTests
{
    private static async Task<(string LibraryId, string MovieId)> FilmAsync(ApplicationTestHost app, string title, int year, string? existingLibraryId = null)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var libraryId = existingLibraryId;
        if (libraryId is null)
        {
            var library = await app.Client.PostAsJsonAsync("/api/libraries/", new
            {
                name = "Films", mediaType = "movies", purpose = "collection", rootPath = root, importWorkflow = "copy",
                autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false
            });
            Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
            libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }
        var filePath = Path.Combine(root, $"{title} ({year})", $"{title} ({year}) [Bluray-1080p].mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId, title, year, wantedStatus: "covered", wantedReason: "Imported from your existing library.",
            currentQuality: "Bluray-1080p", targetQuality: "Bluray-1080p", qualityCutoffMet: true,
            filePath: filePath, fileSizeBytes: 4, cancellationToken: CancellationToken.None);
        return (libraryId, (await movies.ListAsync(CancellationToken.None)).Single(movie => movie.Title == title).Id);
    }

    [Fact]
    public async Task A_rules_titles_list_finds_films_by_name_marks_the_ones_already_on_the_rule_and_answers_404_for_a_rule_that_is_not_there()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, dune) = await FilmAsync(app, "Dune", 2021);
        await FilmAsync(app, "Heat", 1995, libraryId);
        var created = await app.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old films", level = "movie", isEnabled = true, steps = new[] { RetentionStep.KeepOnly("year:max:2100") },
            mediaServerIds = Array.Empty<string>(), removeFromServers = false, waitDays = 7, removalMode = "ask", checkIntervalHours = 8
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ruleId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var before = await app.Client.GetFromJsonAsync<JsonElement>($"/api/retention/rules/{ruleId}/titles?query=dune");
        var found = Assert.Single(before.EnumerateArray());
        Assert.Equal(dune, found.GetProperty("entityId").GetString());
        Assert.Equal("Dune", found.GetProperty("title").GetString());
        Assert.False(found.GetProperty("alreadyListed").GetBoolean());

        var add = await app.Client.PostAsJsonAsync($"/api/retention/rules/{ruleId}/titles", new { entityId = dune });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>($"/api/retention/rules/{ruleId}/titles")).EnumerateArray());
        var after = await app.Client.GetFromJsonAsync<JsonElement>($"/api/retention/rules/{ruleId}/titles?query=e");
        var byTitle = after.EnumerateArray().ToDictionary(item => item.GetProperty("title").GetString()!, item => item.GetProperty("alreadyListed").GetBoolean());
        Assert.True(byTitle["Dune"]);
        Assert.False(byTitle["Heat"]);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/retention/rules/no-such-rule/titles")).StatusCode);
    }

    [Fact]
    public async Task The_collections_list_is_empty_on_a_new_install_and_gives_each_servers_members_skipped_and_the_reason_a_sync_failed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/retention/collections")).Replace(" ", ""));

        var repository = app.Services.GetRequiredService<IRetentionRepository>();
        var attempted = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
        await repository.SaveCollectionStateAsync(new RetentionCollectionState("plex-1", "movies", 3, 1, null, attempted, attempted), CancellationToken.None);
        await repository.SaveCollectionStateAsync(new RetentionCollectionState("jelly-1", "movies", 2, 0, "Jellyfin did not answer.", attempted, null), CancellationToken.None);

        var states = (await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/collections")).EnumerateArray().ToDictionary(item => item.GetProperty("serverId").GetString()!);
        Assert.Equal(2, states.Count);
        Assert.Equal(3, states["plex-1"].GetProperty("members").GetInt32());
        Assert.Equal(1, states["plex-1"].GetProperty("skipped").GetInt32());
        Assert.Equal(JsonValueKind.Null, states["plex-1"].GetProperty("problem").ValueKind);
        Assert.Equal("Jellyfin did not answer.", states["jelly-1"].GetProperty("problem").GetString());
        Assert.Equal(JsonValueKind.Null, states["jelly-1"].GetProperty("syncedUtc").ValueKind);
    }
}
