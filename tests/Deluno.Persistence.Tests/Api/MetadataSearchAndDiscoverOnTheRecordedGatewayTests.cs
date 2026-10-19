using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Add dialog's title search and Discover (coverage register, area library), through the real metadata provider in broker mode
/// reading the project's metadata gateway as it really answered (tests/fixtures/recorded/tmdb: a title search for a film and a
/// show, a search with no matches, and the popular-shows list). Nothing about the gateway is written in the test: the routes,
/// the provider's parsing of the recorded replies, the library markers, the exclusions and the add are all real.
/// </summary>
public sealed class MetadataSearchAndDiscoverOnTheRecordedGatewayTests
{
    private sealed class GatewayReplay : HttpMessageHandler
    {
        public readonly List<string> Asked = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            Asked.Add(path + query);
            string? state = null;
            if (path == "/metadata/search")
            {
                state = query.Contains("Zzqxjv", StringComparison.Ordinal) ? "search-no-results"
                    : query.Contains("mediaType=tv", StringComparison.Ordinal) ? "search-tv" : "search-movie";
            }
            else if (path == "/metadata/tv/popular") state = "popular-tv";
            else if (path == "/metadata/movies/popular") state = "popular-movies";
            return state is null
                ? throw new InvalidOperationException($"No gateway recording answers {request.Method} {path}{query}.")
                : Task.FromResult(RecordedReplies.Tmdb(state).Single.Reply.ToResponse());
        }
    }

    private static Task<ApplicationTestHost> StartAsync(GatewayReplay replay)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddHttpClient<TmdbMetadataProvider>().ConfigurePrimaryHttpMessageHandler(() => replay));

    [Fact]
    public async Task Searching_a_film_by_name_lists_the_record_the_gateway_returned_with_its_year_source_rating_and_poster()
    {
        var replay = new GatewayReplay();
        await using var app = await StartAsync(replay);

        var response = await app.Client.GetAsync("/api/metadata/search?mediaType=movies&query=Sintel&year=2010");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var match = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal("Sintel", match.GetProperty("title").GetString());
        Assert.Equal(2010, match.GetProperty("year").GetInt32());
        Assert.Equal("tmdb", match.GetProperty("provider").GetString());
        Assert.Equal("45745", match.GetProperty("providerId").GetString());
        Assert.Equal(7.333, match.GetProperty("rating").GetDouble(), 3);
        Assert.False(string.IsNullOrWhiteSpace(match.GetProperty("posterUrl").GetString()));
        Assert.Equal(JsonValueKind.Null, match.GetProperty("libraryEntryId").ValueKind);
        Assert.Contains(replay.Asked, asked => asked.StartsWith("/metadata/search?", StringComparison.Ordinal) && asked.Contains("query=Sintel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Searching_a_show_lists_every_match_in_the_gateways_order_and_a_name_nobody_has_lists_nothing_without_an_error()
    {
        await using var app = await StartAsync(new GatewayReplay());

        var shows = await (await app.Client.GetAsync("/api/metadata/search?mediaType=tv&query=Breaking%20Bad")).Content.ReadFromJsonAsync<JsonElement>();
        var none = await app.Client.GetAsync("/api/metadata/search?mediaType=movies&query=Zzqxjv");

        var items = shows.EnumerateArray().ToArray();
        Assert.Equal(5, items.Length);
        Assert.Equal("Breaking Bad", items[0].GetProperty("title").GetString());
        Assert.Equal("1396", items[0].GetProperty("providerId").GetString());
        Assert.Equal(2008, items[0].GetProperty("year").GetInt32());
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Empty((await none.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task The_popular_shows_list_marks_a_show_the_library_holds_and_a_show_that_was_excluded_and_nothing_else()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "tv");
        var held = await app.Client.PostAsJsonAsync("/api/series/", new
        {
            title = "The Simpsons", startYear = 1989, monitored = true, libraryId, metadataProvider = "tmdb", metadataProviderId = "456", searchForMissingNow = false
        });
        Assert.True(held.IsSuccessStatusCode, await held.Content.ReadAsStringAsync());
        var heldId = (await held.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await app.Services.GetRequiredService<IUnifiedExclusionRepository>().UpsertAsync(
            new UpsertMediaExclusionRequest("series", MediaExclusionSourceKinds.ImportList, "list-1", "Some list", "tmdb:108978", "tmdb:108978", "Reacher", 2022, null, null, "Not interested"),
            CancellationToken.None);

        var response = await app.Client.GetAsync("/api/discover?mediaType=series");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var popular = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("popular").EnumerateArray().ToArray();
        Assert.Equal(12, popular.Length);
        JsonElement Card(string title) => popular.Single(card => card.GetProperty("title").GetString() == title);
        Assert.True(Card("The Simpsons").GetProperty("inLibrary").GetBoolean());
        Assert.Equal(heldId, Card("The Simpsons").GetProperty("libraryEntryId").GetString());
        Assert.True(Card("Reacher").GetProperty("excluded").GetBoolean());
        Assert.False(Card("Reacher").GetProperty("inLibrary").GetBoolean());
        Assert.Single(popular, card => card.GetProperty("inLibrary").GetBoolean());
        Assert.Single(popular, card => card.GetProperty("excluded").GetBoolean());
    }

    [Fact]
    public async Task Adding_a_show_from_Discover_keeps_the_library_profile_monitoring_and_provider_record_asks_for_a_search_and_flips_the_card_to_In_library()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "tv");
        var tiers = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-model")).GetProperty("tiers").EnumerateArray().Select(tier => tier.GetProperty("name").GetString()!).ToArray();
        var profile = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "Shows 4K", mediaType = "tv", cutoffQuality = tiers[0], allowedQualities = string.Join(",", tiers), customFormatIds = string.Empty,
            upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        Assert.True(profile.IsSuccessStatusCode, await profile.Content.ReadAsStringAsync());
        var profileId = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var added = await app.Client.PostAsJsonAsync("/api/discover/add", new
        {
            mediaType = "series", title = "Reacher", year = 2022, provider = "tmdb", providerId = "108978", overview = "A drifter.",
            posterUrl = "https://example.invalid/reacher.jpg", libraryId, qualityProfileId = profileId, monitored = true,
            searchForMissingNow = true, searchForCutoffUnmetNow = true
        });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var body = await added.Content.ReadFromJsonAsync<JsonElement>();
        var seriesId = body.GetProperty("id").GetString()!;
        Assert.Equal("series", body.GetProperty("mediaType").GetString());
        Assert.Equal(libraryId, body.GetProperty("libraryId").GetString());
        var stored = (await app.Services.GetRequiredService<ISeriesCatalogRepository>().ListAsync(CancellationToken.None)).Single();
        Assert.Equal(seriesId, stored.Id);
        Assert.Equal("Reacher", stored.Title);
        Assert.Equal(2022, stored.StartYear);
        Assert.True(stored.Monitored);
        Assert.Equal("tmdb", stored.MetadataProvider);
        Assert.Equal("108978", stored.MetadataProviderId);
        Assert.Equal(profileId, await ApiScenes.TextAsync(app, "series", $"SELECT quality_profile_id FROM series_entries WHERE id = '{seriesId}';"));
        Assert.Equal(libraryId, await ApiScenes.TextAsync(app, "series", $"SELECT library_id FROM series_wanted_state WHERE series_id = '{seriesId}';"));
        var refresh = (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None)).Single(job => job.JobType == "series.catalog.refresh");
        Assert.Equal(seriesId, refresh.RelatedEntityId);
        Assert.Contains("pendingSearches", refresh.PayloadJson);
        Assert.Contains("series-added", refresh.PayloadJson);

        var popular = (await app.Client.GetFromJsonAsync<JsonElement>("/api/discover?mediaType=series")).GetProperty("popular").EnumerateArray();
        var card = popular.Single(item => item.GetProperty("title").GetString() == "Reacher");
        Assert.True(card.GetProperty("inLibrary").GetBoolean());
        Assert.Equal(seriesId, card.GetProperty("libraryEntryId").GetString());
    }

    [Fact]
    public async Task A_show_added_with_the_search_box_unticked_asks_for_no_search_and_a_profile_made_for_films_is_refused_in_a_sentence()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "tv");
        var tiers = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-model")).GetProperty("tiers").EnumerateArray().Select(tier => tier.GetProperty("name").GetString()!).ToArray();
        var filmsProfile = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "Films only", mediaType = "movies", cutoffQuality = tiers[0], allowedQualities = string.Join(",", tiers), customFormatIds = string.Empty,
            upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        var filmsProfileId = (await filmsProfile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var refused = await app.Client.PostAsJsonAsync("/api/discover/add", new
        {
            mediaType = "series", title = "Reacher", year = 2022, provider = "tmdb", providerId = "108978", libraryId, qualityProfileId = filmsProfileId, monitored = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Choose a quality profile made for this kind of title.", await refused.Content.ReadAsStringAsync());
        Assert.Empty(await app.Services.GetRequiredService<ISeriesCatalogRepository>().ListAsync(CancellationToken.None));

        var quiet = await app.Client.PostAsJsonAsync("/api/discover/add", new
        {
            mediaType = "tv", title = "Reacher", year = 2022, provider = "tmdb", providerId = "108978", libraryId, monitored = true, searchForMissingNow = false, searchForCutoffUnmetNow = false
        });
        Assert.Equal(HttpStatusCode.Created, quiet.StatusCode);
        var refresh = (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None)).Single(job => job.JobType == "series.catalog.refresh");
        using var payload = JsonDocument.Parse(refresh.PayloadJson!);
        Assert.Empty(payload.RootElement.GetProperty("pendingSearches").EnumerateArray());
    }
}
