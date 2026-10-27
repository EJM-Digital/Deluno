using System.Net;
using Deluno.Integrations.MediaServers;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// #1301: the one call every media server request goes through (MediaServerHttp.SendAsync), against what Jellyfin 12.2.0 and
/// Emby 4.10.1.0 really answered (tests/fixtures/recorded): a readable answer, a refused token, other refusals, and a server
/// that does not answer. Each is one attempt, with a sentence in plain words and the status the server gave.
/// </summary>
public sealed class MediaServerHttpOnRecordedRepliesTests
{
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public int Made { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(MediaServerHttp.ClientName, name);
            Made++;
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private static readonly Dictionary<string, string> Token = new() { ["X-Emby-Token"] = "a-token" };

    private static Task<MediaServerCallResult> SendAsync(Factory factory, HttpMethod method, string path, string serverName = "Jellyfin")
        => MediaServerHttp.SendAsync(factory, method, new Uri("http://127.0.0.1:8096" + path), Token, serverName, CancellationToken.None);

    [Fact]
    public async Task A_readable_answer_is_a_success_that_carries_the_body_and_the_status()
    {
        var recorded = RecordedReplies.Jellyfin("identity");
        var asked = new List<HttpRequestMessage>();
        var factory = new Factory(RecordedReplies.Handler("jellyfin", [recorded], (request, _) => { asked.Add(request); return null; }));

        var result = await SendAsync(factory, HttpMethod.Get, "/System/Info/Public");

        Assert.True(result.Ok);
        Assert.Null(result.Problem);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(recorded.Exchanges[0].Reply.Json()["Version"]!.GetValue<string>(), System.Text.Json.JsonDocument.Parse(result.Body!).RootElement.GetProperty("Version").GetString());
        Assert.Equal("a-token", Assert.Single(asked).Headers.GetValues("X-Emby-Token").Single());
        Assert.Equal(1, factory.Made);
    }

    [Theory]
    [InlineData("jellyfin", "Jellyfin", "/System/Info")]
    [InlineData("emby", "Emby", "/emby/System/Info")]
    public async Task A_refused_token_is_said_as_that_with_its_status_and_is_never_an_empty_answer(string app, string name, string path)
    {
        var recording = app == "jellyfin" ? RecordedReplies.Jellyfin("wrong-token") : RecordedReplies.Emby("wrong-token");
        var factory = new Factory(RecordedReplies.Handler(app, [recording]));

        var result = await SendAsync(factory, HttpMethod.Get, path, name);

        Assert.False(result.Ok);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal($"{name} refused the token (HTTP 401).", result.Problem);
        Assert.Null(result.Body);
        Assert.Equal(1, factory.Made);
    }

    [Theory]
    [InlineData("jellyfin", "/Library/Media/Updated", 400)]
    [InlineData("emby", "/emby/Library/Media/Updated", 500)]
    public async Task Any_other_refusal_is_said_with_the_status_the_server_gave(string app, string path, int status)
    {
        var recording = app == "jellyfin" ? RecordedReplies.Jellyfin("media-updated-refused") : RecordedReplies.Emby("media-updated-refused");
        var factory = new Factory(RecordedReplies.Handler(app, [recording]));

        var result = await SendAsync(factory, HttpMethod.Post, path, app == "jellyfin" ? "Jellyfin" : "Emby");

        Assert.False(result.Ok);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal($"{(app == "jellyfin" ? "Jellyfin" : "Emby")} answered HTTP {status}.", result.Problem);
        Assert.Equal(1, factory.Made);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_is_said_to_be_unreachable_with_no_status_and_one_attempt()
    {
        var factory = new Factory(RecordedReplies.Jellyfin("not-answering").NotAnswering());

        var result = await SendAsync(factory, HttpMethod.Get, "/System/Info");

        Assert.False(result.Ok);
        Assert.Null(result.StatusCode);
        Assert.Equal("Deluno could not reach Jellyfin.", result.Problem);
        Assert.Equal(1, factory.Made);
    }

    [Fact]
    public async Task A_caller_that_gives_up_is_cancelled_and_not_told_the_server_is_down()
    {
        var factory = new Factory(RecordedReplies.Jellyfin("not-answering").NotAnswering());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MediaServerHttp.SendAsync(factory, HttpMethod.Get, new Uri("http://127.0.0.1:8096/System/Info"), Token, "Jellyfin", cancelled.Token));
    }
}
