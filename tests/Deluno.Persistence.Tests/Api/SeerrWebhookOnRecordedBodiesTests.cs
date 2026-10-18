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
                    services.AddSingleton<ISeerrClient>(new NoopSeerrClient());
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
    public async Task The_default_payload_of_an_approval_carries_no_Seerr_media_id_so_the_route_acknowledges_it_and_acts_on_nothing()
    {
        // Seerr 3.5.0's media object has media_type, tmdbId, tvdbId and the statuses: no media id (webhook-media-id-not-substituted shows that
        // asking for {{media_id}} sends the text unchanged). Deluno needs the id to mark the title available later, so it ignores the call.
        var sent = R("webhook-approved").Exchanges[0];
        var media = System.Text.Json.Nodes.JsonNode.Parse(sent.Request.Body!)!["media"]!.AsObject();
        Assert.False(media.ContainsKey("media_id") || media.ContainsKey("id"));
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(Posted(connection.Id, sent, secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_acquisitions.Calls);
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
    public async Task A_payload_that_asks_for_the_media_id_gets_the_placeholder_text_back_from_Seerr_and_is_ignored()
    {
        var sent = R("webhook-media-id-not-substituted").Exchanges[0];
        Assert.Contains("{{media_id}}", sent.Request.Body, StringComparison.Ordinal);
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(Posted(connection.Id, sent, secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_acquisitions.Calls);
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
