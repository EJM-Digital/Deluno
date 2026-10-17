using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Deluno.Integrations.Metadata.SceneMapping;
using Deluno.Tests.Recorded;
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
    /// <summary>
    /// TheXEM's real answer for a show it maps (tests/fixtures/recorded/thexem, TVDB 79824, shortened): the provider asks the address the
    /// recording was made at, and keeps the entries that say something the provider's own numbering does not (a different scene season or
    /// episode, or an absolute number). The expected entries are worked out from the recorded body here, not written by hand.
    /// </summary>
    [Fact]
    public async Task A_show_TheXEM_maps_gives_the_entries_whose_scene_numbering_or_absolute_number_says_something_more()
    {
        var recording = RecordedReplies.Thexem("map-all-mapped");
        var asked = new List<string>();
        var provider = Provider(request =>
        {
            asked.Add(request.RequestUri!.PathAndQuery);
            return recording.Exchanges[0].Reply.ToResponse();
        });

        var entries = await provider.GetMappingAsync("79824", CancellationToken.None);

        Assert.Equal(recording.Exchanges[0].Request.Path, Assert.Single(asked));
        var recorded = JsonNode.Parse(recording.Exchanges[0].Reply.Body!)!["data"]!.AsArray();
        var expected = recorded.Where(item =>
        {
            var tvdb = item!["tvdb"]!;
            var scene = item["scene"];
            var differs = scene?["season"] is not null && scene["episode"] is not null
                && (scene["season"]!.GetValue<int>() != tvdb["season"]!.GetValue<int>() || scene["episode"]!.GetValue<int>() != tvdb["episode"]!.GetValue<int>());
            return differs || tvdb["absolute"] is not null || scene?["absolute"] is not null;
        }).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Length, entries.Count);
        Assert.Contains(recorded, item => item!["scene"]!["season"]!.GetValue<int>() != item["tvdb"]!["season"]!.GetValue<int>());
        var first = entries[0];
        Assert.Equal(expected[0]!["tvdb"]!["season"]!.GetValue<int>(), first.ProviderSeasonNumber);
        Assert.Equal(expected[0]!["tvdb"]!["episode"]!.GetValue<int>(), first.ProviderEpisodeNumber);
        Assert.Equal(expected[0]!["tvdb"]!["absolute"]!.GetValue<int>(), first.AbsoluteNumber);
        Assert.Contains(entries, entry => entry.SceneSeasonNumber is not null && entry.SceneSeasonNumber != entry.ProviderSeasonNumber);
    }

    /// <summary>TheXEM answers most titles with HTTP 200 and <c>"result": "failure"</c> and an empty list (recorded for a show and for id 0): nothing to correct.</summary>
    [Theory]
    [InlineData("map-all-unknown-show", "81189")]
    [InlineData("map-all-id-zero", "0")]
    public async Task A_title_with_no_mapping_answers_empty_rather_than_throwing(string state, string tvdbId)
    {
        var recording = RecordedReplies.Thexem(state);
        var provider = Provider(request =>
        {
            Assert.Equal(recording.Exchanges[0].Request.Path, request.RequestUri!.PathAndQuery);
            return recording.Exchanges[0].Reply.ToResponse();
        });

        var entries = await provider.GetMappingAsync(tvdbId, CancellationToken.None);

        Assert.Equal("failure", JsonNode.Parse(recording.Exchanges[0].Reply.Body!)!["result"]!.GetValue<string>());
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
