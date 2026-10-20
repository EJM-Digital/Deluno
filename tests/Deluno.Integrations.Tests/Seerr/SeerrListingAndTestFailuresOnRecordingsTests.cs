using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Integrations.Seerr;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Seerr;

/// <summary>
/// How Deluno's Seerr client answers when Seerr will not give the request list or the test (register: integrations 3, "List: 401 wrong key /
/// timeout / 429", "List: reply that is not a results list", "Test: server not answering / timeout"). The refusals are the replies Seerr 3.5.0
/// really gave (tests/fixtures/recorded/seerr: wrong-key 403, not-set-up 401); a reply that is not a list is a recorded reply of another
/// call (the film lookup) given where a list should be; a rate limit and a timeout are a status line and the exception the HTTP client raises,
/// since no app body is involved. A list that cannot be read is Unreachable, never "nobody requested anything".
/// </summary>
public sealed class SeerrListingAndTestFailuresOnRecordingsTests
{
    private static SeerrConnection Server()
        => new("seerr-1", "Seerr", "http://192.168.1.51:5055", "key-123", true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static SeerrClient Answering(Func<HttpRequestMessage, HttpResponseMessage?> answer)
        => new(new Factory(RecordedReplies.Handler("seerr", [], (request, _) => answer(request) ?? throw new InvalidOperationException($"No answer for {request.RequestUri!.PathAndQuery}."))));

    [Theory]
    [InlineData("wrong-key", 1, 403)]
    [InlineData("not-set-up", 2, 401)]
    public async Task A_key_Seerr_refuses_on_the_request_list_is_unreachable_with_the_refusal_named_not_an_empty_list(string state, int exchange, int refusal)
    {
        Assert.Equal(refusal, RecordedReplies.Seerr(state).Exchanges[exchange].Reply.Status);
        var client = Answering(_ => RecordedReplies.Seerr(state).Exchanges[exchange].Reply.ToResponse());

        var answer = await client.ListRequestsAsync(Server(), CancellationToken.None);

        var unreachable = Assert.IsType<MediaServerAnswer<SeerrLibrary>.Unreachable>(answer);
        Assert.Contains($"refused the token (HTTP {refusal})", unreachable.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Seerr_that_answers_429_is_unreachable_with_the_status_named_and_asked_once()
    {
        var asked = 0;
        var client = Answering(_ =>
        {
            asked++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });

        var answer = await client.ListRequestsAsync(Server(), CancellationToken.None);

        Assert.Contains("answered HTTP 429", Assert.IsType<MediaServerAnswer<SeerrLibrary>.Unreachable>(answer).Reason, StringComparison.Ordinal);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task A_Seerr_that_never_answers_before_the_timeout_is_unreachable_for_the_list_and_the_test_alike()
    {
        // What HttpClient raises when its own timeout elapses; the caller's token is not the one cancelled.
        var client = Answering(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout elapsing.", new TimeoutException()));

        var list = await client.ListRequestsAsync(Server(), CancellationToken.None);
        var test = await client.TestAsync(Server(), CancellationToken.None);

        Assert.Contains("could not reach Seerr", Assert.IsType<MediaServerAnswer<SeerrLibrary>.Unreachable>(list).Reason, StringComparison.Ordinal);
        Assert.False(test.Ok);
        Assert.Equal("unreachable", test.Status);
    }

    [Fact]
    public async Task A_reply_that_is_not_a_results_list_is_unreachable_and_says_so_not_an_empty_list()
    {
        // The recorded film lookup answered where a list should be: a JSON object, but no "results".
        var notAList = RecordedReplies.Seerr("request-pending").Exchanges[3].Reply;
        Assert.Null(notAList.Json()["results"]);
        var client = Answering(request => request.RequestUri!.AbsolutePath == "/api/v1/request" ? notAList.ToResponse() : null);

        var answer = await client.ListRequestsAsync(Server(), CancellationToken.None);

        Assert.Contains("a list Deluno could not read", Assert.IsType<MediaServerAnswer<SeerrLibrary>.Unreachable>(answer).Reason, StringComparison.Ordinal);
    }
}
