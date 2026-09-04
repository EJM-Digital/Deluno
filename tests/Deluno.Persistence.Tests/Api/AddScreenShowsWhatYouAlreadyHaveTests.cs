using Deluno.Integrations.Metadata;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Going to add something you already have shows you that you have it (#424).
///
/// <para><b>The duplicate was never the problem.</b> Adding a held title has
/// always returned the existing row rather than a second one. The problem was
/// that the Add screen looked identical either way: search, six cards, "Click
/// to select" under every one of them, including the film sitting in your
/// library. The only way to learn was to add it and see nothing change.</para>
///
/// <para>These drive the real route table, so they hold the parts that are only
/// true when assembled: that the composition registers something able to
/// answer, that the search endpoint asks it, and that the answer reaches the
/// browser as <c>libraryEntryId</c> - the field the Add dialog reads. A test
/// of the marker alone would pass with the endpoint never calling it.</para>
///
/// <para>The metadata provider is the one thing replaced. The real one reaches
/// TMDb over the network, which a test must not, and whose answers are not
/// Deluno's to assert.</para>
/// </summary>
public sealed class AddScreenShowsWhatYouAlreadyHaveTests
{
    [Fact]
    public async Task A_film_you_already_have_carries_the_entry_it_would_open()
    {
        await using var app = await StartWithResultsAsync(
            Result("movies", "Arrival", 2016, "tt2543164", "329865"),
            Result("movies", "Sicario", 2015, "tt3397884", "273481"));

        var held = await AddAsync(app, "/api/movies/", new { title = "Arrival", releaseYear = 2016, monitored = true });

        var results = await SearchAsync(app, "movies", "arrival");

        Assert.Equal(held, LibraryEntryId(results, "Arrival"));
        Assert.Null(LibraryEntryId(results, "Sicario"));
    }

    [Fact]
    public async Task A_show_you_already_have_carries_the_entry_it_would_open()
    {
        await using var app = await StartWithResultsAsync(
            Result("tv", "Severance", 2022, "tt11280740", "95396"),
            Result("tv", "Silo", 2023, "tt14688458", "125988"));

        var held = await AddAsync(app, "/api/series/", new { title = "Severance", startYear = 2022, monitored = true });

        var results = await SearchAsync(app, "tv", "sev");

        Assert.Equal(held, LibraryEntryId(results, "Severance"));
        Assert.Null(LibraryEntryId(results, "Silo"));
    }

    /// <summary>
    /// An empty catalogue marks nothing - the state every install starts in, and
    /// the one a marker that defaulted to "held" would ruin.
    /// </summary>
    [Theory]
    [InlineData("movies", "Arrival")]
    [InlineData("tv", "Severance")]
    public async Task Nothing_is_marked_when_the_library_is_empty(string mediaType, string title)
    {
        await using var app = await StartWithResultsAsync(Result(mediaType, title, 2016, "tt2543164", "329865"));

        Assert.Null(LibraryEntryId(await SearchAsync(app, mediaType, title), title));
    }

    /// <summary>
    /// The id is the one that opens the title, not merely a true-ish flag: the
    /// dialog navigates to it, so a wrong id is a worse answer than none.
    /// </summary>
    [Theory]
    [InlineData("movies", "/api/movies/")]
    [InlineData("tv", "/api/series/")]
    public async Task The_entry_it_names_is_the_one_the_catalogue_serves(string mediaType, string addRoute)
    {
        await using var app = await StartWithResultsAsync(Result(mediaType, "Arrival", 2016, "tt2543164", "329865"));

        // The two routes name the year differently - `releaseYear` for a film,
        // `startYear` for a show. Sending the wrong one drops the year silently,
        // and a marker with nothing to match on looks exactly like a broken one.
        object body = mediaType == "movies"
            ? new { title = "Arrival", releaseYear = 2016, monitored = true }
            : new { title = "Arrival", startYear = 2016, monitored = true };
        var held = await AddAsync(app, addRoute, body);

        var marked = LibraryEntryId(await SearchAsync(app, mediaType, "arrival"), "Arrival");
        Assert.Equal(held, marked);

        var detail = await app.Client.GetAsync($"{addRoute.TrimEnd('/')}/{marked}");
        Assert.True(
            detail.IsSuccessStatusCode,
            $"The id the Add screen offered returned {(int)detail.StatusCode} from the catalogue.");
    }

    /// <summary>
    /// A result whose title reads nothing like the stored row is still held when
    /// the IMDb id matches - because that is what the Add would decide, and the
    /// screen has to agree with the button.
    /// </summary>
    [Fact]
    public async Task A_film_recognised_only_by_its_imdb_id_is_still_marked()
    {
        await using var app = await StartWithResultsAsync(
            Result("movies", "Arrival (Original Motion Picture)", null, "tt2543164", "999999"));

        var held = await AddAsync(
            app,
            "/api/movies/",
            new { title = "Arrival", releaseYear = 2016, imdbId = "tt2543164", monitored = true });

        Assert.Equal(held, LibraryEntryId(await SearchAsync(app, "movies", "arrival"), "Arrival (Original Motion Picture)"));
    }

    // ------------------------------------------------------------------ helpers

    private static Task<ApplicationTestHost> StartWithResultsAsync(params MetadataSearchResult[] results)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IMetadataProvider>();
            services.AddSingleton<IMetadataProvider>(new RecordingMetadataProvider(results));
        });

    private static async Task<JsonElement> SearchAsync(ApplicationTestHost app, string mediaType, string query)
    {
        var response = await app.Client.GetAsync($"/api/metadata/search?mediaType={mediaType}&query={Uri.EscapeDataString(query)}");
        Assert.True(
            response.IsSuccessStatusCode,
            $"GET /api/metadata/search returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>
    /// Reads the field the Add dialog reads. Named rather than indexed so a
    /// reordered result list cannot make a failing assertion pass.
    /// </summary>
    private static string? LibraryEntryId(JsonElement results, string title)
    {
        foreach (var result in results.EnumerateArray())
        {
            if (result.GetProperty("title").GetString() == title)
            {
                return result.TryGetProperty("libraryEntryId", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
            }
        }

        Assert.Fail($"The search response carried no result titled '{title}'.");
        return null;
    }

    private static async Task<string> AddAsync(ApplicationTestHost app, string route, object body)
    {
        var response = await app.Client.PostAsJsonAsync(route, body);
        Assert.True(
            response.IsSuccessStatusCode,
            $"POST {route} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static MetadataSearchResult Result(
        string mediaType,
        string title,
        int? year,
        string? imdbId,
        string providerId)
        => new(
            "tmdb",
            providerId,
            mediaType,
            title,
            OriginalTitle: null,
            year,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Rating: null,
            [],
            [],
            imdbId,
            ExternalUrl: null);
}
