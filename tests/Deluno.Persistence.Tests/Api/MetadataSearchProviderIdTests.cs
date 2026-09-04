using Deluno.Integrations.Metadata;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Asking for one title by its provider id gets that title, not a search.
///
/// <para><b>The endpoint used to throw the id away.</b>
/// <c>GET /api/metadata/search</c> bound <c>query</c>, <c>mediaType</c> and
/// <c>year</c> and then built its lookup with a hardcoded <c>null</c> provider
/// id - while two callers were sending one. Both exist to upgrade a chosen
/// search card into the provider's full detail record before it is stored:
/// <c>enrichMetadataResult</c> on the library screen and
/// <c>enrichSelectedMetadata</c> in the setup guide.</para>
///
/// <para>So the "enrich" call re-ran the same title search, and the client's
/// <c>details.find(item =&gt; item.providerId === result.providerId) ?? result</c>
/// quietly handed back the card it already had. It looked like it worked. Every
/// title was stored with card-level metadata and no cast, crew, runtime or
/// certification - the same shape as the three defects found on 2026-09-03,
/// where a call answered the question next to the one it appeared to answer.</para>
///
/// <para>Measured against the managed broker before the fix, the two calls were
/// indistinguishable: <c>?mediaType=movies&amp;query=arrival</c> and the same
/// with <c>&amp;providerId=329865</c> both returned six cards with no cast and
/// no runtime. Asked directly, that gateway answers the second with one record
/// carrying 30 cast, 20 crew and a 116 minute runtime.</para>
/// </summary>
public sealed class MetadataSearchProviderIdTests
{
    [Theory]
    [InlineData("movies", "329865")]
    [InlineData("tv", "95396")]
    public async Task A_provider_id_reaches_the_provider_instead_of_being_dropped(string mediaType, string providerId)
    {
        var provider = new RecordingMetadataProvider(
            [Card(mediaType, "Arrival", "329865"), Card(mediaType, "Severance", "95396")],
            new Dictionary<string, MetadataSearchResult>
            {
                [providerId] = Detail(mediaType, "The detail record", providerId)
            });
        await using var app = await StartWithAsync(provider);

        await SearchAsync(app, $"?mediaType={mediaType}&query=anything&providerId={providerId}");

        var request = Assert.Single(provider.Requests);
        Assert.Equal(providerId, request.ProviderId);
    }

    /// <summary>
    /// The point of sending the id: one exact record rather than a page of
    /// candidates. Held on the response, not only on what the provider was
    /// asked, because a parameter that arrives and changes nothing is the
    /// defect in a different place.
    /// </summary>
    [Theory]
    [InlineData("movies", "329865")]
    [InlineData("tv", "95396")]
    public async Task The_answer_is_the_one_record_that_id_names(string mediaType, string providerId)
    {
        var provider = new RecordingMetadataProvider(
            [Card(mediaType, "Arrival", "329865"), Card(mediaType, "Severance", "95396")],
            new Dictionary<string, MetadataSearchResult>
            {
                [providerId] = Detail(mediaType, "The detail record", providerId)
            });
        await using var app = await StartWithAsync(provider);

        var cards = await SearchAsync(app, $"?mediaType={mediaType}&query=anything");
        var detail = await SearchAsync(app, $"?mediaType={mediaType}&query=anything&providerId={providerId}");

        Assert.Equal(2, cards.GetArrayLength());
        var only = Assert.Single(detail.EnumerateArray().ToArray());
        Assert.Equal("The detail record", only.GetProperty("title").GetString());
        Assert.Equal(providerId, only.GetProperty("providerId").GetString());
        // What the enrich call exists to fetch, and what a search card has none of.
        Assert.NotEmpty(only.GetProperty("cast").EnumerateArray().ToArray());
        Assert.Equal(116, only.GetProperty("runtimeMinutes").GetInt32());
    }

    /// <summary>
    /// No id, no change. The plain search is what the Add screen runs on every
    /// keystroke, and it must stay a search.
    /// </summary>
    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    public async Task A_search_without_an_id_still_asks_for_nothing_in_particular(string mediaType)
    {
        var provider = new RecordingMetadataProvider([Card(mediaType, "Arrival", "329865")]);
        await using var app = await StartWithAsync(provider);

        await SearchAsync(app, $"?mediaType={mediaType}&query=arrival&year=2016");

        var request = Assert.Single(provider.Requests);
        Assert.Null(request.ProviderId);
        Assert.Equal("arrival", request.Query);
        Assert.Equal(2016, request.Year);
    }

    /// <summary>
    /// A supplied id is an identity assertion, so an id the provider no longer
    /// has answers with nothing rather than with a similarly named film. The
    /// caller keeps the card it started with; it does not silently relink.
    /// </summary>
    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    public async Task An_id_the_provider_no_longer_has_answers_with_nothing(string mediaType)
    {
        var provider = new RecordingMetadataProvider([Card(mediaType, "Arrival", "329865")]);
        await using var app = await StartWithAsync(provider);

        var results = await SearchAsync(app, $"?mediaType={mediaType}&query=arrival&providerId=999999999");

        Assert.Equal(0, results.GetArrayLength());
        Assert.Equal("999999999", Assert.Single(provider.Requests).ProviderId);
    }

    // ------------------------------------------------------------------ helpers

    private static Task<ApplicationTestHost> StartWithAsync(RecordingMetadataProvider provider)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IMetadataProvider>();
            services.AddSingleton<IMetadataProvider>(provider);
        });

    private static async Task<JsonElement> SearchAsync(ApplicationTestHost app, string queryString)
    {
        var response = await app.Client.GetAsync($"/api/metadata/search{queryString}");
        Assert.True(
            response.IsSuccessStatusCode,
            $"GET /api/metadata/search{queryString} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>What a title search returns: enough to draw a card, no more.</summary>
    private static MetadataSearchResult Card(string mediaType, string title, string providerId)
        => new(
            "tmdb",
            providerId,
            mediaType,
            title,
            OriginalTitle: null,
            2016,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Rating: null,
            [],
            [],
            ImdbId: null,
            ExternalUrl: null);

    /// <summary>What a lookup by id returns: the record the card was missing.</summary>
    private static MetadataSearchResult Detail(string mediaType, string title, string providerId)
        => Card(mediaType, title, providerId) with
        {
            Cast = [new MetadataCastMember("Amy Adams", "Louise Banks", null, "12851", null)],
            Crew = [new MetadataCrewMember("Denis Villeneuve", "Director", null, "137427", null)],
            RuntimeMinutes = 116,
            Certification = "PG-13"
        };
}
