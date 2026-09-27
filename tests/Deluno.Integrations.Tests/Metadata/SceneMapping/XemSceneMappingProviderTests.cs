using System.Net;
using System.Text;
using Deluno.Integrations.Metadata.SceneMapping;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Metadata.SceneMapping;

/// <summary>
/// #863: TheXEM is asked by TVDB id and answers most titles with
/// <c>"result": "failure"</c> — a title with nothing to correct, not a
/// reachability problem. Only entries where the scene numbering (or an
/// absolute number) actually says something the provider's own season/episode
/// does not are kept.
/// </summary>
public sealed class XemSceneMappingProviderTests
{
    [Fact]
    public async Task A_scene_mismatch_is_reported_and_an_identical_entry_is_not()
    {
        var body = """
        {
          "result": "success",
          "data": [
            { "tvdb": { "season": 1, "episode": 13, "absolute": 13 }, "scene": { "season": 1, "episode": 1 } },
            { "tvdb": { "season": 1, "episode": 1 }, "scene": { "season": 1, "episode": 1 } }
          ]
        }
        """;

        var provider = Provider(_ => Json(body));

        var entries = await provider.GetMappingAsync("12345", CancellationToken.None);

        var mismatch = Assert.Single(entries);
        Assert.Equal(1, mismatch.ProviderSeasonNumber);
        Assert.Equal(13, mismatch.ProviderEpisodeNumber);
        Assert.Equal(1, mismatch.SceneSeasonNumber);
        Assert.Equal(1, mismatch.SceneEpisodeNumber);
        Assert.Equal(13, mismatch.AbsoluteNumber);
    }

    [Fact]
    public async Task A_title_with_no_mapping_answers_empty_rather_than_throwing()
    {
        var provider = Provider(_ => Json("""{"result":"failure","message":"No data found for this show"}"""));

        var entries = await provider.GetMappingAsync("999999", CancellationToken.None);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task An_unreachable_source_answers_empty_rather_than_throwing()
    {
        var provider = Provider(_ => throw new HttpRequestException("connection refused"));

        var entries = await provider.GetMappingAsync("12345", CancellationToken.None);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task A_server_error_answers_empty_rather_than_throwing()
    {
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var entries = await provider.GetMappingAsync("12345", CancellationToken.None);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task A_blank_tvdb_id_is_not_asked_at_all()
    {
        var asked = false;
        var provider = Provider(_ =>
        {
            asked = true;
            return Json("""{"result":"failure"}""");
        });

        var entries = await provider.GetMappingAsync("   ", CancellationToken.None);

        Assert.Empty(entries);
        Assert.False(asked);
    }

    private static XemSceneMappingProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(Factory(send), NullLogger<XemSceneMappingProvider>.Instance);

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static IHttpClientFactory Factory(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new StubFactory(new HttpClient(new StubHandler(send)));

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
