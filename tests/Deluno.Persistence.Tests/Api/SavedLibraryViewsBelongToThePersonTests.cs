using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Saved library views belong to the person who saved them: asking without signing in is refused, the list defaults to
/// the movies views, and another person neither sees nor removes them (coverage register, area engines).
/// </summary>
public sealed class SavedLibraryViewsBelongToThePersonTests
{
    private static object View(string variant, string name) => new
    {
        variant,
        libraryId = (string?)null,
        name,
        quickFilter = "all",
        monitoring = "any",
        sortField = "title",
        sortDirection = "asc",
        viewMode = "poster",
        cardSize = "medium",
        displayOptionsJson = "{}",
        rulesJson = "[]"
    };

    [Fact]
    public async Task Without_signing_in_the_views_cannot_be_listed_or_saved()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/library-views")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/library-views", View("movies", "Nope"))).StatusCode);
    }

    [Fact]
    public async Task The_list_defaults_to_the_movies_views_and_each_variant_keeps_its_own()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        (await app.Client.PostAsJsonAsync("/api/library-views", View("movies", "Films to watch"))).EnsureSuccessStatusCode();
        (await app.Client.PostAsJsonAsync("/api/library-views", View("shows", "Shows to catch up"))).EnsureSuccessStatusCode();

        var byDefault = await app.Client.GetFromJsonAsync<JsonElement>("/api/library-views");
        var shows = await app.Client.GetFromJsonAsync<JsonElement>("/api/library-views?variant=shows");

        Assert.Equal("Films to watch", Assert.Single(byDefault.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("Shows to catch up", Assert.Single(shows.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Another_person_neither_sees_nor_removes_a_view_and_the_owner_removes_it_with_a_delete()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/library-views", View("movies", "Mine"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var repository = app.Services.GetRequiredService<ILibrariesRepository>();

        Assert.Empty(await repository.ListLibraryViewsAsync("someone-else", "movies", CancellationToken.None));
        Assert.False(await repository.DeleteLibraryViewAsync("someone-else", id, CancellationToken.None));
        Assert.Single(await app.Client.GetFromJsonAsync<JsonElement[]>("/api/library-views?variant=movies") ?? []);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/library-views/{id}")).StatusCode);
        Assert.Empty(await app.Client.GetFromJsonAsync<JsonElement[]>("/api/library-views?variant=movies") ?? []);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/library-views/{id}")).StatusCode);
    }
}
