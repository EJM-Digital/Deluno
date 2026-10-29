using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The metadata routes of the real host: what the add-title box sends the provider, and what "Check now" records about the metadata
/// service. The service's answers are the replies the project's deployed metadata gateway really gave (tests/fixtures/recorded/tmdb);
/// a service that does not answer is a 503 with no body, as the gateway's own outage answer.
/// </summary>
public sealed class EnginesMetadataRowsOnTheRecordedGatewayTests
{
    private sealed class Gateway(bool answering) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!answering)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var path = request.RequestUri!.AbsolutePath;
            return path == "/metadata/search"
                ? Task.FromResult(RecordedReplies.Tmdb("search-movie").Single.Reply.ToResponse())
                : throw new HttpRequestException($"No gateway recording answers {request.Method} {request.RequestUri.PathAndQuery}.");
        }
    }

    [Theory]
    [InlineData("tmdb", "movies", "45745")]
    [InlineData("imdb", "movies", "tt1727587")]
    [InlineData("tvdb", "tv", "81189")]
    public async Task The_add_title_box_passes_the_kind_of_id_it_was_typed_with_through_to_the_provider(string idSource, string mediaType, string providerId)
    {
        var provider = new RecordingMetadataProvider([]);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IMetadataProvider>();
            services.AddSingleton<IMetadataProvider>(provider);
        });

        var response = await app.Client.GetAsync($"/api/metadata/search?mediaType={mediaType}&query={idSource}:{providerId}&providerId={providerId}&idSource={idSource}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var request = Assert.Single(provider.Requests);
        Assert.Equal(idSource, request.IdSource);
        Assert.Equal(providerId, request.ProviderId);

        // Nothing typed with a prefix: no kind of id is invented.
        provider.Requests.Clear();
        await app.Client.GetAsync($"/api/metadata/search?mediaType={mediaType}&query=Sintel");
        Assert.Null(Assert.Single(provider.Requests).IdSource);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Check_now_records_what_the_metadata_service_really_answered_as_a_metadata_connection_check(bool serviceAnswers)
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddHttpClient<TmdbMetadataProvider>().ConfigurePrimaryHttpMessageHandler(() => new Gateway(serviceAnswers)));
        Assert.Empty(await ChecksAsync(app));

        var response = await app.Client.PostAsJsonAsync("/api/metadata/test", new { query = "Sintel", mediaType = "movies", year = 2010 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var check = Assert.Single(await ChecksAsync(app));
        Assert.Equal("Metadata", check.GetProperty("kind").GetString(), ignoreCase: true);
        Assert.Equal("metadata", check.GetProperty("id").GetString());
        Assert.Equal(serviceAnswers, check.GetProperty("ok").GetBoolean());
        if (serviceAnswers)
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty("failure").ValueKind);
            Assert.True(body.GetProperty("resultCount").GetInt32() > 0);
        }
        else
        {
            Assert.NotEqual(JsonValueKind.Null, body.GetProperty("failure").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("message").GetString()));
        }
    }

    private static async Task<JsonElement[]> ChecksAsync(ApplicationTestHost app)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/connections/checks")).EnumerateArray()
            .Where(item => item.GetProperty("id").GetString() == "metadata").ToArray();
}
