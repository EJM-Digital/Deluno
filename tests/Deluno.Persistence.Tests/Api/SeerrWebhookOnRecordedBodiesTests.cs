using System.Net;
using System.Net.Http.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.MediaServers;
using Deluno.Integrations.Seerr;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The inbound Seerr webhook (#851) against the bodies Seerr 3.5.0 itself sent, recorded from the real app in Docker with its default
/// payload (tests/fixtures/recorded/seerr, #1237): the request is what Seerr posted, with only the Authorization header set to the
/// connection's own secret, as a person pastes it into Seerr's webhook settings.
/// </summary>
public sealed class SeerrWebhookOnRecordedBodiesTests : IAsyncDisposable
{
    private readonly TestStorage _storage;
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly SqliteSeerrRepository _repository;
    private readonly FixedTimeProvider _timeProvider;
    private readonly RecordingAcquisitionService _acquisitions = new();
    private readonly NoopSeerrClient _seerr = new();

    public SeerrWebhookOnRecordedBodiesTests()
    {
        _storage = TestStorage.Create();
        _timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(_storage.Factory, _timeProvider);
        new PlatformSchemaInitializer(_storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        _repository = new SqliteSeerrRepository(_storage.Factory, _timeProvider, TestSecretProtection.Create(_storage));

        var builder = new HostBuilder().ConfigureWebHost(webBuilder =>
        {
            webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<TimeProvider>(_timeProvider);
                    services.AddSingleton<ISeerrRepository>(_repository);
                    services.AddSingleton<ISeerrClient>(_seerr);
                    services.AddSingleton<ISeerrAcquisitionsRepository>(
                        new SqliteSeerrAcquisitionsRepository(_storage.Factory, _timeProvider));
                    services.AddSingleton<ISeerrAcquisitionService>(_acquisitions);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapDelunoSeerr());
                });
        });

        _host = builder.Build();
        _host.StartAsync().GetAwaiter().GetResult();
        _client = _host.GetTestServer().CreateClient();
    }

    private async Task<SeerrConnection> CreateConnectionAsync()
        => (await _repository.SaveAsync(
            null,
            new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-1", IsEnabled: true),
            CancellationToken.None))!;

    private static Recording R(string state) => RecordedReplies.Seerr(state);

    /// <summary>What Seerr posted (the recorded body and content type), sent to the route with the given Authorization value.</summary>
    private static HttpRequestMessage Posted(string connectionId, Exchange sent, string? authorization, Func<string, string>? edit = null)
    {
        var body = sent.Request.Body!;
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/seerr-servers/{connectionId}/webhook")
        {
            Content = new StringContent(edit is null ? body : edit(body), System.Text.Encoding.UTF8, "application/json")
        };
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return request;
    }

    private static string WithMediaId(string body, string mediaId)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(body)!;
        json["media"]!["media_id"] = mediaId;
        return json.ToJsonString();
    }

    [Fact]
    public async Task The_default_payload_of_an_approval_carries_no_Seerr_media_id_so_Deluno_asks_Seerr_for_it_and_adds_the_title()
    {
        // Seerr 3.5.0's media object has media_type, tmdbId, tvdbId and the statuses: no media id (#1356). Deluno finds it from the TMDb id.
        var sent = R("webhook-approved").Exchanges[0];
        var media = System.Text.Json.Nodes.JsonNode.Parse(sent.Request.Body!)!["media"]!.AsObject();
        Assert.False(media.ContainsKey("media_id") || media.ContainsKey("id"));
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);
        _seerr.FoundId = 7;

        var response = await _client.SendAsync(Posted(connection.Id, sent, secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(_acquisitions.Calls);
        Assert.Equal("45745", call.TmdbId);
        Assert.Equal(7, call.SeerrMediaId);
        Assert.Equal(("movies", "45745"), (_seerr.Asked!.Value.MediaType, _seerr.Asked.Value.TmdbId));
    }

    [Fact]
    public async Task An_approval_is_still_added_when_Seerr_cannot_say_its_media_id_just_now()
    {
        var sent = R("webhook-approved").Exchanges[0];
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);
        _seerr.FoundId = null;

        var response = await _client.SendAsync(Posted(connection.Id, sent, secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, Assert.Single(_acquisitions.Calls).SeerrMediaId);
    }

    [Fact]
    public async Task The_same_approval_with_a_media_id_added_reaches_the_acquisition_with_the_ids_the_request_and_who_asked()
    {
        var sent = R("webhook-approved").Exchanges[0];
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(Posted(connection.Id, sent, secret, body => WithMediaId(body, "1")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(_acquisitions.Calls);
        Assert.Equal("movies", call.MediaType);
        Assert.Equal("45745", call.TmdbId);
        Assert.True(string.IsNullOrEmpty(call.TvdbId), "Seerr sends an empty tvdbId for a film");
        Assert.Equal(1, call.SeerrMediaId);
        Assert.False(call.Is4k);
        Assert.False(string.IsNullOrWhiteSpace(call.RequestedBy));
        Assert.Equal("Movie Request Approved", (string?)System.Text.Json.Nodes.JsonNode.Parse(sent.Request.Body!)!["event"]);
    }

    [Fact]
    public async Task An_administrators_own_request_is_sent_as_MEDIA_AUTO_APPROVED_and_is_taken_like_an_approval_once_it_has_an_id()
    {
        var sent = R("webhook-auto-approved").Exchanges[0];
        Assert.Equal("MEDIA_AUTO_APPROVED", (string?)System.Text.Json.Nodes.JsonNode.Parse(sent.Request.Body!)!["notification_type"]);
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(Posted(connection.Id, sent, secret, body => WithMediaId(body, "3")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(_acquisitions.Calls);
        Assert.Equal("603", call.TmdbId);
        Assert.Equal(3, call.SeerrMediaId);
    }

    [Fact]
    public async Task A_payload_that_asks_for_the_media_id_gets_the_placeholder_text_back_from_Seerr_and_is_added_by_its_TMDb_id()
    {
        var sent = R("webhook-media-id-not-substituted").Exchanges[0];
        Assert.Contains("{{media_id}}", sent.Request.Body, StringComparison.Ordinal);
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);
        _seerr.FoundId = 3;

        var response = await _client.SendAsync(Posted(connection.Id, sent, secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(_acquisitions.Calls);
        Assert.Equal("550", call.TmdbId);
        Assert.Equal(3, call.SeerrMediaId);
    }

    /// <summary>A notification that is not an approval (a request waiting, one declined, the test button) is accepted so Seerr does not retry, and never acted on.</summary>
    [Theory]
    [InlineData("webhook-pending", 0)]
    [InlineData("webhook-declined", 1)]
    [InlineData("webhook-test", 0)]
    public async Task What_Seerr_sends_for_a_request_waiting_declined_or_its_test_button_is_accepted_and_ignored(string state, int exchange)
    {
        var sent = R(state).Exchanges[exchange];
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(Posted(connection.Id, sent, secret, body => WithMediaIdWhenPresent(body, "1")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_acquisitions.Calls);
    }

    private static string WithMediaIdWhenPresent(string body, string mediaId)
        => System.Text.Json.Nodes.JsonNode.Parse(body)!["media"] is null ? body : WithMediaId(body, mediaId);

    [Fact]
    public async Task The_test_button_sends_a_TEST_NOTIFICATION_with_null_media_and_the_route_still_answers_200()
    {
        var sent = R("webhook-test").Exchanges[0];
        var json = System.Text.Json.Nodes.JsonNode.Parse(sent.Request.Body!)!;
        Assert.Equal("TEST_NOTIFICATION", (string?)json["notification_type"]);
        Assert.Null(json["media"]);
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Posted(connection.Id, sent, secret))).StatusCode);
    }

    [Fact]
    public async Task What_Seerr_sends_without_the_secret_in_the_Authorization_header_is_refused()
    {
        var sent = R("webhook-approved").Exchanges[0];
        var connection = await CreateConnectionAsync();
        await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Posted(connection.Id, sent, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Posted(connection.Id, sent, "not-the-secret"))).StatusCode);
        Assert.True(sent.Request.Headers!.ContainsKey("authorization"));
    }

    private static string Edited(string body, Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        edit(json);
        return json.ToJsonString();
    }

    /// <summary>The recorded series request Seerr sent (a declined one, the only series webhook recorded) as it sends an approval: only the type and the event text differ.</summary>
    private static string ApprovedSeries(string body)
        => Edited(body, json =>
        {
            json["notification_type"] = "MEDIA_APPROVED";
            json["event"] = "Series Request Approved";
        });

    [Fact]
    public async Task A_connection_that_never_had_a_secret_generated_refuses_what_Seerr_sends_whatever_the_Authorization_header_says()
    {
        var sent = R("webhook-approved").Exchanges[0];
        var connection = await CreateConnectionAsync();
        Assert.False(connection.HasWebhookSecret);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Posted(connection.Id, sent, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Posted(connection.Id, sent, "anything"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Posted(connection.Id, sent, ""))).StatusCode);
        Assert.Empty(_acquisitions.Calls);
    }

    [Fact]
    public async Task Regenerating_the_secret_makes_Seerrs_old_Authorization_value_stop_working_at_once()
    {
        var sent = R("webhook-approved").Exchanges[0];
        var connection = await CreateConnectionAsync();
        var first = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Posted(connection.Id, sent, first, body => WithMediaId(body, "1")))).StatusCode);

        var second = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Posted(connection.Id, sent, first, body => WithMediaId(body, "1")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Posted(connection.Id, sent, second, body => WithMediaId(body, "1")))).StatusCode);
        Assert.Equal(2, _acquisitions.Calls.Count);
    }

    [Fact]
    public async Task A_connection_that_does_not_exist_or_is_switched_off_answers_404_and_adds_nothing()
    {
        var sent = R("webhook-approved").Exchanges[0];
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(Posted("no-such-connection", sent, secret, body => WithMediaId(body, "1")))).StatusCode);

        await _repository.SaveAsync(connection.Id, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", null, IsEnabled: false), CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(Posted(connection.Id, sent, secret, body => WithMediaId(body, "1")))).StatusCode);
        Assert.Empty(_acquisitions.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("this is not json")]
    [InlineData("{\"notification_type\": ")]
    public async Task A_body_that_is_empty_or_not_JSON_is_a_400_with_a_plain_message_after_the_secret_was_accepted(string body)
    {
        var sent = R("webhook-approved").Exchanges[0];
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(Posted(connection.Id, sent, secret, _ => body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var said = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.False(string.IsNullOrWhiteSpace((string?)said["message"]));
        Assert.Empty(_acquisitions.Calls);
    }

    [Fact]
    public async Task An_approval_with_no_media_object_is_acknowledged_and_nothing_is_added()
    {
        var sent = R("webhook-approved").Exchanges[0];
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(Posted(connection.Id, sent, secret, body => Edited(body, json => json.Remove("media"))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_acquisitions.Calls);
    }

    [Fact]
    public async Task A_series_request_is_taken_as_tv_with_its_TVDb_id_and_a_film_or_anything_else_as_movies()
    {
        var series = R("webhook-declined").Exchanges[2];
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Posted(connection.Id, series, secret, body => WithMediaId(ApprovedSeries(body), "2")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Posted(connection.Id, R("webhook-approved").Exchanges[0], secret, body => WithMediaId(body, "1")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Posted(connection.Id, R("webhook-approved").Exchanges[0], secret, body => WithMediaId(Edited(body, json => json["media"]!["media_type"] = "music"), "1")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Posted(connection.Id, R("webhook-approved").Exchanges[0], secret, body => WithMediaId(Edited(body, json => json["media"]!.AsObject().Remove("media_type")), "1")))).StatusCode);

        Assert.Equal(["tv", "movies", "movies", "movies"], _acquisitions.Calls.Select(call => call.MediaType));
        var tv = _acquisitions.Calls[0];
        Assert.Equal(("1396", "81189", 2), (tv.TmdbId, tv.TvdbId, tv.SeerrMediaId));
    }

    [Fact]
    public async Task The_media_id_is_read_from_media_id_first_then_from_id_and_only_asked_of_Seerr_when_neither_is_a_number()
    {
        var sent = R("webhook-approved").Exchanges[0];
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);
        _seerr.FoundId = 77;

        await _client.SendAsync(Posted(connection.Id, sent, secret, body => Edited(body, json => { json["media"]!["media_id"] = 4; json["media"]!["id"] = 9; })));
        await _client.SendAsync(Posted(connection.Id, sent, secret, body => Edited(body, json => json["media"]!["id"] = "9")));
        Assert.Null(_seerr.Asked);
        await _client.SendAsync(Posted(connection.Id, sent, secret, body => Edited(body, json => { json["media"]!["media_id"] = "{{media_id}}"; json["media"]!["id"] = "{{id}}"; })));

        Assert.Equal([4, 9, 77], _acquisitions.Calls.Select(call => call.SeerrMediaId));
        Assert.Equal(("movies", "45745"), (_seerr.Asked!.Value.MediaType, _seerr.Asked.Value.TmdbId));
    }

    [Theory]
    [InlineData("Movie Request Approved", null, false)]
    [InlineData("4K Movie Request Approved", null, true)]
    [InlineData("4K Movie Request Approved", false, false)]
    [InlineData("Movie Request Approved", true, true)]
    [InlineData("Movie Request Approved", "true", true)]
    [InlineData("4K Movie Request Approved", "false", false)]
    public async Task A_4K_request_is_told_from_the_event_text_and_an_explicit_is4k_in_a_custom_payload_overrides_it(string eventText, object? explicitIs4k, bool expected)
    {
        var sent = R("webhook-approved").Exchanges[0];
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        await _client.SendAsync(Posted(connection.Id, sent, secret, body => WithMediaId(Edited(body, json =>
        {
            json["event"] = eventText;
            if (explicitIs4k is bool flag) json["is4k"] = flag;
            if (explicitIs4k is string text) json["is_4k"] = text;
        }), "1")));

        Assert.Equal(expected, Assert.Single(_acquisitions.Calls).Is4k);
    }

    [Fact]
    public async Task The_TMDb_and_TVDb_ids_are_read_whether_Seerr_sends_them_as_text_or_as_numbers()
    {
        var series = R("webhook-declined").Exchanges[2];
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        await _client.SendAsync(Posted(connection.Id, series, secret, body => WithMediaId(ApprovedSeries(Edited(body, json => { json["media"]!["tmdbId"] = 1396; json["media"]!["tvdbId"] = 81189; })), "2")));
        await _client.SendAsync(Posted(connection.Id, series, secret, body => WithMediaId(ApprovedSeries(Edited(body, json => json["media"]!.AsObject().Remove("tmdbId"))), "2")));

        Assert.Equal([("1396", "81189"), (null, "81189")], _acquisitions.Calls.Select(call => (call.TmdbId, call.TvdbId)));
    }

    [Fact]
    public async Task A_webhook_secret_saved_on_another_computer_cannot_be_read_so_it_counts_as_not_generated_and_every_call_is_refused_until_it_is_made_again()
    {
        var sent = R("webhook-approved").Exchanges[0];
        var elsewhere = new SqliteSeerrRepository(_storage.Factory, _timeProvider, new Deluno.Security.DataProtectionSecretProtector(
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_storage.DataRoot, "keys-elsewhere")))));
        var connection = (await elsewhere.SaveAsync(null, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-1", IsEnabled: true), CancellationToken.None))!;
        var oldSecret = await elsewhere.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var here = await _repository.GetAsync(connection.Id, CancellationToken.None);

        Assert.False(here!.HasWebhookSecret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Posted(connection.Id, sent, oldSecret, body => WithMediaId(body, "1")))).StatusCode);
        Assert.Empty(_acquisitions.Calls);

        var fresh = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Posted(connection.Id, sent, fresh, body => WithMediaId(body, "1")))).StatusCode);
        Assert.Single(_acquisitions.Calls);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _storage.Dispose();
    }

    private sealed class NoopSeerrClient : ISeerrClient
    {
        public Task<SeerrTestResult> TestAsync(SeerrConnection connection, CancellationToken cancellationToken)
            => Task.FromResult(new SeerrTestResult(true, "healthy", "ok", 1));

        public Task<MediaServerAnswer<SeerrLibrary>> ListRequestsAsync(SeerrConnection connection, CancellationToken cancellationToken)
            => Task.FromResult<MediaServerAnswer<SeerrLibrary>>(new MediaServerAnswer<SeerrLibrary>.Unreachable("not used"));

        public Task<MediaServerAnswer<SeerrClearing>> ClearAsync(SeerrConnection connection, string mediaType, string? tmdbId, string? tvdbId, CancellationToken cancellationToken)
            => Task.FromResult<MediaServerAnswer<SeerrClearing>>(new MediaServerAnswer<SeerrClearing>.Unreachable("not used"));

        public Task<MediaServerAnswer<bool>> MarkAvailableAsync(SeerrConnection connection, int seerrMediaId, CancellationToken cancellationToken)
            => Task.FromResult<MediaServerAnswer<bool>>(new MediaServerAnswer<bool>.Unreachable("not used"));

        public int? FoundId { get; set; }

        public (string MediaType, string? TmdbId)? Asked { get; private set; }

        public Task<int?> FindMediaIdAsync(SeerrConnection connection, string mediaType, string? tmdbId, string? tvdbId, CancellationToken cancellationToken)
        {
            Asked = (mediaType, tmdbId);
            return Task.FromResult(FoundId);
        }
    }

    /// <summary>
    /// #851: records every call it actually gets, so a test can prove an
    /// approval reached <see cref="AcquireAsync"/> -- and, just as important,
    /// that a non-approval payload never does.
    /// </summary>
    private sealed class RecordingAcquisitionService : ISeerrAcquisitionService
    {
        public List<(string MediaType, string? TmdbId, string? TvdbId, int SeerrMediaId, bool Is4k, string? RequestedBy)> Calls { get; } = [];

        public Task<SeerrAcquisitionOutcome> AcquireAsync(SeerrConnection connection, string mediaType, string? tmdbId, string? tvdbId, int seerrMediaId, bool is4k, string? requestedBy, CancellationToken cancellationToken)
        {
            Calls.Add((mediaType, tmdbId, tvdbId, seerrMediaId, is4k, requestedBy));
            return Task.FromResult(new SeerrAcquisitionOutcome(true, "added"));
        }
    }
}
