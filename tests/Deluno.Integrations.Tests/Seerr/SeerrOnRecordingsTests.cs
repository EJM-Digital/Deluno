using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Integrations.Seerr;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Seerr;

/// <summary>
/// Deluno's Seerr client against Seerr 3.5.0's own replies, recorded from the real app in Docker (tests/fixtures/recorded/seerr, #1237),
/// signed in with a Jellyfin administrator and with an ordinary user making the requests, so they begin as pending. A call is answered by
/// the recorded reply to the same address; the ids in a call are the test's own.
/// </summary>
public sealed class SeerrOnRecordingsTests
{
    private static SeerrConnection Server(string? key = "key-123")
        => new("seerr-1", "Seerr", "http://192.168.1.51:5055", key, true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Seerr(Func<HttpRequestMessage, HttpResponseMessage?> answer)
    {
        public List<string> Calls { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public SeerrClient Client() => new(new Factory(RecordedReplies.Handler("seerr", [], (request, _) =>
        {
            Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            Requests.Add(request);
            return answer(request) ?? throw new InvalidOperationException($"No Seerr recording answers {request.Method} {request.RequestUri!.PathAndQuery}.");
        })));

        public bool Sent(string fragment) => Calls.Any(call => call.Contains(fragment, StringComparison.Ordinal));
    }

    private static Recording R(string state) => RecordedReplies.Seerr(state);

    private static HttpResponseMessage Reply(string state, int exchange) => R(state).Exchanges[exchange].Reply.ToResponse();

    // ---- the connection test ----

    [Fact]
    public async Task A_test_proves_the_key_with_the_settings_and_says_which_server_and_version()
    {
        var seerr = new Seerr(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/status" => Reply("status", 0),
            "/api/v1/settings/main" => Reply("settings-main", 0),
            _ => null
        });

        var result = await seerr.Client().TestAsync(Server(), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("3.5.0", result.Version);
        Assert.Equal("Seerr", result.ServerName);
        Assert.All(seerr.Requests, request => Assert.Equal("key-123", Assert.Single(request.Headers.GetValues("X-Api-Key"))));
    }

    [Fact]
    public async Task A_key_Seerr_does_not_know_is_refused_with_403_on_every_call_and_the_test_says_so()
    {
        var refused = R("wrong-key");
        Assert.All(refused.Exchanges, exchange => Assert.Equal(403, exchange.Reply.Status));
        var seerr = new Seerr(request => request.RequestUri!.AbsolutePath == "/api/v1/status" ? Reply("status", 0) : refused.Exchanges[0].Reply.ToResponse());

        var test = await seerr.Client().TestAsync(Server("wrong"), CancellationToken.None);
        var listing = await new Seerr(_ => refused.Exchanges[1].Reply.ToResponse()).Client().ListRequestsAsync(Server("wrong"), CancellationToken.None);

        Assert.False(test.Ok);
        Assert.Contains("refused the token", test.Message, StringComparison.Ordinal);
        Assert.Equal("3.5.0", test.Version);
        var unreachable = Assert.IsType<MediaServerAnswer<SeerrLibrary>.Unreachable>(listing);
        Assert.Contains("refused the token", unreachable.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Seerr_nobody_has_set_up_yet_answers_the_status_but_refuses_the_settings_with_401()
    {
        var before = R("not-set-up");
        Assert.False((bool)before.Exchanges[0].Reply.Json()["initialized"]!);
        Assert.Equal(401, before.Exchanges[1].Reply.Status);
        var seerr = new Seerr(request => request.RequestUri!.AbsolutePath == "/api/v1/status" ? Reply("status", 0) : before.Exchanges[1].Reply.ToResponse());

        var result = await seerr.Client().TestAsync(Server(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("degraded", result.Status);
    }

    [Fact]
    public void The_first_sign_in_makes_the_first_administrator_and_the_settings_then_carry_the_key()
    {
        var setup = R("setup");

        Assert.Equal(200, setup.Exchanges[0].Reply.Status);
        Assert.Equal(1, (int)setup.Exchanges[0].Reply.Json()["id"]!);
        Assert.Equal(2, (int)setup.Exchanges[0].Reply.Json()["permissions"]!);
        Assert.False(string.IsNullOrWhiteSpace((string?)R("settings-main").Single.Reply.Json()["applicationTitle"]));
    }

    // ---- the request and user lists ----

    private static HttpResponseMessage? Lists(HttpRequestMessage request, string state, int requests, int users)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path switch
        {
            "/api/v1/request" => Reply(state, requests),
            "/api/v1/user" => Reply(state, users),
            _ => null
        };
    }

    [Fact]
    public async Task With_no_request_made_the_library_is_empty_and_the_administrator_is_the_one_user()
    {
        var answer = await new Seerr(request => Lists(request, "requests-empty", 0, 1)).Client().ListRequestsAsync(Server(), CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<SeerrLibrary>.Reported>(answer).Value;
        Assert.Empty(library.Requests);
        Assert.Single(library.Users);
    }

    [Fact]
    public async Task A_pending_request_is_listed_with_who_asked_and_when_and_is_not_approved()
    {
        var answer = await new Seerr(request => Lists(request, "request-pending", 1, 2)).Client().ListRequestsAsync(Server(), CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<SeerrLibrary>.Reported>(answer).Value;
        var pending = Assert.Single(library.Requests);
        Assert.Equal("movies", pending.MediaType);
        Assert.Equal("45745", pending.TmdbId);
        Assert.Null(pending.ApprovedUtc);
        Assert.NotEqual("Unknown user", pending.RequestedBy);
        Assert.NotEmpty(library.Users);
    }

    [Fact]
    public async Task An_approved_request_has_the_time_it_was_approved()
    {
        var answer = await new Seerr(request => request.RequestUri!.AbsolutePath == "/api/v1/request" ? Reply("request-approved", 1) : Reply("request-pending", 2)).Client().ListRequestsAsync(Server(), CancellationToken.None);

        var approved = Assert.Single(Assert.IsType<MediaServerAnswer<SeerrLibrary>.Reported>(answer).Value.Requests);
        Assert.NotNull(approved.ApprovedUtc);
    }

    [Fact]
    public async Task A_declined_series_request_is_listed_with_its_ids_and_is_not_approved()
    {
        var answer = await new Seerr(request => request.RequestUri!.AbsolutePath == "/api/v1/request" ? Reply("request-declined-tv", 2) : Reply("request-pending", 2)).Client().ListRequestsAsync(Server(), CancellationToken.None);

        var declined = Assert.Single(Assert.IsType<MediaServerAnswer<SeerrLibrary>.Reported>(answer).Value.Requests);
        Assert.Equal("tv", declined.MediaType);
        Assert.Equal("1396", declined.TmdbId);
        Assert.Null(declined.ApprovedUtc);
    }

    [Fact]
    public async Task An_administrators_own_request_is_approved_at_once()
    {
        var answer = await new Seerr(request => request.RequestUri!.AbsolutePath == "/api/v1/request" ? Reply("request-auto-approved", 1) : Reply("request-pending", 2)).Client().ListRequestsAsync(Server(), CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<SeerrLibrary>.Reported>(answer).Value;
        var own = Assert.Single(library.Requests, request => request.TmdbId == "603");
        Assert.NotNull(own.ApprovedUtc);
    }

    // ---- clearing a title from Seerr, marking it available ----

    [Fact]
    public async Task A_title_Seerr_holds_is_found_by_its_TMDb_id_and_its_media_record_deleted()
    {
        var seerr = new Seerr(request =>
        {
            if (request.Method == HttpMethod.Delete) return Reply("media-deleted", 0);
            return request.RequestUri!.AbsolutePath == "/api/v1/movie/45745" ? Reply("request-pending", 3) : null;
        });

        var answer = await seerr.Client().ClearAsync(Server(), "movies", "45745", null, CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<SeerrClearing>.Reported>(answer).Value.Cleared);
        Assert.True(seerr.Sent("GET /api/v1/movie/45745"));
        Assert.True(seerr.Sent("DELETE /api/v1/media/1"), string.Join(" | ", seerr.Calls));
    }

    [Fact]
    public async Task A_title_Seerr_never_heard_of_has_no_record_to_clear_and_nothing_is_deleted()
    {
        var seerr = new Seerr(request => request.RequestUri!.AbsolutePath == "/api/v1/movie/45745" ? Reply("lookup-unknown", 0) : null);

        var answer = await seerr.Client().ClearAsync(Server(), "movies", "45745", null, CancellationToken.None);

        var cleared = Assert.IsType<MediaServerAnswer<SeerrClearing>.Reported>(answer).Value;
        Assert.False(cleared.Cleared);
        Assert.DoesNotContain(seerr.Calls, call => call.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_series_lookup_by_TMDb_id_with_no_media_record_clears_nothing()
    {
        var seerr = new Seerr(request => request.RequestUri!.AbsolutePath == "/api/v1/tv/1396" ? Reply("lookup-unknown", 1) : null);

        var answer = await seerr.Client().ClearAsync(Server(), "tv", "1396", null, CancellationToken.None);

        Assert.False(Assert.IsType<MediaServerAnswer<SeerrClearing>.Reported>(answer).Value.Cleared);
    }

    [Fact]
    public async Task A_TMDb_id_Seerr_cannot_retrieve_answers_500_and_the_title_could_not_be_checked_not_cleared()
    {
        Assert.Equal(500, R("lookup-unknown").Exchanges[2].Reply.Status);
        var seerr = new Seerr(_ => Reply("lookup-unknown", 2));

        var answer = await seerr.Client().ClearAsync(Server(), "movies", "999999999", null, CancellationToken.None);

        Assert.IsType<MediaServerAnswer<SeerrClearing>.Unreachable>(answer);
    }

    [Fact]
    public async Task A_second_delete_of_the_same_media_record_is_still_cleared_because_Seerr_answers_204_again()
    {
        Assert.Equal(204, R("media-deleted").Exchanges[3].Reply.Status);
        var seerr = new Seerr(request => request.Method == HttpMethod.Delete ? Reply("media-deleted", 3) : Reply("request-pending", 3));

        var answer = await seerr.Client().ClearAsync(Server(), "movies", "45745", null, CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<SeerrClearing>.Reported>(answer).Value.Cleared);
        Assert.Empty(R("media-deleted").Exchanges[1].Reply.Json()["results"]!.AsArray());
    }

    [Fact]
    public async Task Marking_a_record_available_sends_the_JSON_body_the_real_Seerr_takes_and_is_told_it_was_taken()
    {
        var recording = R("media-available");
        Assert.Equal(415, recording.Exchanges[0].Reply.Status);
        string? sentBody = null;
        string? sentType = null;
        var seerr = new Seerr(request =>
        {
            sentBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            sentType = request.Content?.Headers.ContentType?.MediaType;
            return recording.Exchanges[sentType == "application/json" ? 1 : 0].Reply.ToResponse();
        });

        var answer = await seerr.Client().MarkAvailableAsync(Server(), 1, CancellationToken.None);

        // Seerr 3.5.0 refuses a call with no body (415) and takes {"is4k": false} as JSON (#1356).
        Assert.True(Assert.IsType<MediaServerAnswer<bool>.Reported>(answer).Value);
        Assert.True(seerr.Sent("POST /api/v1/media/1/available"));
        Assert.Equal("application/json", sentType);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(recording.Exchanges[1].Request.Body!), System.Text.Json.Nodes.JsonNode.Parse(sentBody!)), "Deluno sends the body the recorded accepted call sent.");
    }

    [Fact]
    public async Task A_Seerr_that_still_refuses_the_mark_is_reported_as_could_not()
    {
        var recording = R("media-available");
        var seerr = new Seerr(_ => recording.Exchanges[0].Reply.ToResponse());

        Assert.IsType<MediaServerAnswer<bool>.Unreachable>(await seerr.Client().MarkAvailableAsync(Server(), 1, CancellationToken.None));
    }

    [Fact]
    public async Task The_Seerr_media_id_of_a_film_is_found_from_its_TMDb_id_when_the_webhook_did_not_carry_it()
    {
        var seerr = new Seerr(request => request.RequestUri!.AbsolutePath == "/api/v1/movie/45745" ? Reply("media-available", 3) : null);

        Assert.Equal(1, await seerr.Client().FindMediaIdAsync(Server(), "movies", "45745", null, CancellationToken.None));
        Assert.True(seerr.Sent("GET /api/v1/movie/45745"));
    }

    [Fact]
    public async Task The_Seerr_media_id_is_not_found_for_a_title_Seerr_has_no_record_of_or_a_Seerr_that_does_not_answer()
    {
        var unknown = R("lookup-unknown").Exchanges[0].Reply.ToResponse();
        Assert.Null(await new Seerr(_ => unknown).Client().FindMediaIdAsync(Server(), "movies", "1", null, CancellationToken.None));
        var client = new SeerrClient(new Factory(R("not-answering").NotAnswering()));
        Assert.Null(await client.FindMediaIdAsync(Server(), "movies", "45745", null, CancellationToken.None));
    }

    [Fact]
    public async Task A_Seerr_that_does_not_answer_could_not_be_asked_on_every_call()
    {
        var client = new SeerrClient(new Factory(R("not-answering").NotAnswering()));

        Assert.False((await client.TestAsync(Server(), CancellationToken.None)).Ok);
        Assert.IsType<MediaServerAnswer<SeerrLibrary>.Unreachable>(await client.ListRequestsAsync(Server(), CancellationToken.None));
        Assert.IsType<MediaServerAnswer<SeerrClearing>.Unreachable>(await client.ClearAsync(Server(), "movies", "45745", null, CancellationToken.None));
        Assert.IsType<MediaServerAnswer<bool>.Unreachable>(await client.MarkAvailableAsync(Server(), 1, CancellationToken.None));
    }

    // ---- the webhook bodies Seerr itself sends are pinned by the webhook route's own tests (SeerrWebhookOnRecordedBodiesTests) ----

    [Fact]
    public void Every_webhook_Seerr_sent_is_a_JSON_body_with_a_type_and_an_authorization_header()
    {
        foreach (var state in new[] { "webhook-test", "webhook-pending", "webhook-approved", "webhook-declined", "webhook-auto-approved", "webhook-media-id-not-substituted" })
        {
            foreach (var exchange in R(state).Exchanges)
            {
                Assert.Equal("application/json", exchange.Request.Headers!["content-type"]);
                Assert.True(exchange.Request.Headers.ContainsKey("authorization"));
                Assert.False(string.IsNullOrWhiteSpace((string?)System.Text.Json.Nodes.JsonNode.Parse(exchange.Request.Body!)!["notification_type"]));
            }
        }

        Assert.Equal(HttpStatusCode.OK, R("webhook-approved").Exchanges[0].Reply.StatusCode);
    }
}
