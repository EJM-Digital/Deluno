using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A standalone disk-space address and a find-by-IMDb-id address (#1021), in
/// the shapes of Radarr's <c>DiskSpaceController</c> and
/// <c>GET /movie/lookup/imdb</c>.
/// </summary>
public sealed class DiskSpaceAndImdbLookupEndpointTests
{
    [Fact]
    public async Task Disk_space_lists_each_drive_Deluno_uses_with_Radarrs_field_names()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var made = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());

        var response = await app.Client.GetAsync("/api/diskspace");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var drives = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(drives.GetArrayLength() >= 1);
        var first = drives[0];
        Assert.Equal("Deluno's own data", first.GetProperty("label").GetString());
        Assert.True(first.GetProperty("totalSpace").GetInt64() > 0);
        Assert.InRange(first.GetProperty("freeSpace").GetInt64(), 0, first.GetProperty("totalSpace").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task A_library_on_the_same_drive_as_the_data_folder_is_not_listed_twice()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root });

        var drives = await app.Client.GetFromJsonAsync<JsonElement>("/api/diskspace");

        Assert.Equal(1, drives.GetArrayLength());
    }

    [Fact]
    public async Task Disk_space_needs_a_sign_in_and_a_read_key_is_enough()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/diskspace")).StatusCode);

        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Dashboard", scopes = "read" });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var dashboard = app.CreateAnonymousClient();
        dashboard.DefaultRequestHeaders.Add("X-Api-Key", key);
        Assert.Equal(HttpStatusCode.OK, (await dashboard.GetAsync("/api/diskspace")).StatusCode);
    }

    [Fact]
    public async Task An_imdb_id_finds_that_film_and_says_whether_Deluno_already_holds_it()
    {
        await using var app = await StartWithFilmAsync("tt2543164", "Arrival", 2016);

        var before = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/lookup/imdb?imdbId=tt2543164");
        Assert.Equal("Arrival", before.GetProperty("title").GetString());
        Assert.Equal("tt2543164", before.GetProperty("imdbId").GetString());
        Assert.False(before.TryGetProperty("libraryEntryId", out var none) && none.ValueKind == JsonValueKind.String);

        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Arrival", releaseYear = 2016, imdbId = "tt2543164", monitored = true });
        var heldId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var after = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/lookup/imdb?imdbId=tt2543164");
        Assert.Equal(heldId, after.GetProperty("libraryEntryId").GetString());
    }

    [Fact]
    public async Task The_lookup_asks_for_an_identity_not_a_title_search()
    {
        var provider = new RecordingMetadataProvider([], new Dictionary<string, MetadataSearchResult> { ["tt2543164"] = Film("tt2543164", "Arrival", 2016) });
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IMetadataProvider>();
            services.AddSingleton<IMetadataProvider>(provider);
        });

        await app.Client.GetAsync("/api/movies/lookup/imdb?imdbId=TT2543164");

        var request = Assert.Single(provider.Requests);
        Assert.Equal("imdb", request.IdSource);
        Assert.Equal("TT2543164", request.ProviderId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2543164")]
    [InlineData("tt12")]
    [InlineData("nm0000001")]
    public async Task A_malformed_imdb_id_is_a_400(string id)
    {
        await using var app = await StartWithFilmAsync("tt2543164", "Arrival", 2016);

        var response = await app.Client.GetAsync($"/api/movies/lookup/imdb?imdbId={id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_imdb_id_nobody_has_is_a_404()
    {
        await using var app = await StartWithFilmAsync("tt2543164", "Arrival", 2016);

        var response = await app.Client.GetAsync("/api/movies/lookup/imdb?imdbId=tt9999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_id_that_names_a_show_is_not_a_film_and_is_a_404_here()
    {
        var provider = new RecordingMetadataProvider(
            [],
            new Dictionary<string, MetadataSearchResult> { ["tt11280740"] = Film("tt11280740", "Severance", 2022) with { MediaType = "tv" } });
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IMetadataProvider>();
            services.AddSingleton<IMetadataProvider>(provider);
        });

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/movies/lookup/imdb?imdbId=tt11280740")).StatusCode);
    }

    private static Task<ApplicationTestHost> StartWithFilmAsync(string imdbId, string title, int year)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IMetadataProvider>();
            services.AddSingleton<IMetadataProvider>(new RecordingMetadataProvider(
                [],
                new Dictionary<string, MetadataSearchResult> { [imdbId] = Film(imdbId, title, year) }));
        });

    private static MetadataSearchResult Film(string imdbId, string title, int year)
        => new("tmdb", "329865", "movies", title, null, year, null, null, null, null, [], [], imdbId, null);
}
