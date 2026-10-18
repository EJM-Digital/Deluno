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

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The inbound Seerr webhook (#851) has no Deluno session behind it — it is
/// explicitly public (<see cref="Deluno.Security.DelunoPublicEndpointAttribute"/>)
/// and authenticates itself by checking the Authorization header Overseerr
/// sends back against the connection's own generated secret.
/// </summary>
public sealed class SeerrWebhookEndpointTests : IAsyncDisposable
{
    private readonly TestStorage _storage;
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly SqliteSeerrRepository _repository;
    private readonly FixedTimeProvider _timeProvider;
    private readonly RecordingAcquisitionService _acquisitions = new();

    public SeerrWebhookEndpointTests()
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

    private static HttpRequestMessage ApprovedPayload(string connectionId, string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/seerr-servers/{connectionId}/webhook")
        {
            Content = JsonContent.Create(new
            {
                notification_type = "MEDIA_APPROVED",
                media = new { media_type = "movie", tmdbId = "603", media_id = "1" }
            })
        };
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return request;
    }

    [Fact]
    public async Task Webhook_refuses_a_connection_with_no_secret_generated_yet()
    {
        var connection = await CreateConnectionAsync();

        var response = await _client.SendAsync(ApprovedPayload(connection.Id, "anything"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_refuses_a_missing_authorization_header()
    {
        var connection = await CreateConnectionAsync();
        await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(ApprovedPayload(connection.Id, authorization: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_refuses_the_wrong_secret()
    {
        var connection = await CreateConnectionAsync();
        await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(ApprovedPayload(connection.Id, "not-the-right-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_accepts_the_right_secret()
    {
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(ApprovedPayload(connection.Id, secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>#851: an approval payload actually reaches the acquisition service, not just a 200.</summary>
    [Fact]
    public async Task An_approval_payload_reaches_acquireasync()
    {
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(ApprovedPayload(connection.Id, secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(_acquisitions.Calls);
        Assert.Equal("movies", call.MediaType);
        Assert.Equal("603", call.TmdbId);
        Assert.Equal(1, call.SeerrMediaId);
        Assert.False(call.Is4k);
    }

    /// <summary>
    /// #985: Jellyseerr/Overseerr's webhook JSON has no "is4k" field of its
    /// own — MediaRequest.sendNotification prefixes the "event" string "4K "
    /// whenever the request's own is4k column is true, and that is the only
    /// place the flag shows through the default payload template.
    /// </summary>
    [Fact]
    public async Task A_4k_approval_is_recognised_from_the_event_text()
    {
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/seerr-servers/{connection.Id}/webhook")
        {
            Content = JsonContent.Create(new
            {
                notification_type = "MEDIA_APPROVED",
                @event = "4K Movie Request Approved",
                media = new { media_type = "movie", tmdbId = "603", media_id = "1" },
                request = new { requestedBy_username = "James" }
            })
        };
        request.Headers.TryAddWithoutValidation("Authorization", secret);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(_acquisitions.Calls);
        Assert.True(call.Is4k);
        Assert.Equal("James", call.RequestedBy);
    }

    /// <summary>#851: a notification that is not an approval is accepted (so Overseerr does not retry it) but never acted on.</summary>
    [Theory]
    [InlineData("MEDIA_PENDING")]
    [InlineData("MEDIA_DECLINED")]
    [InlineData("TEST_NOTIFICATION")]
    public async Task A_non_approval_payload_is_ignored(string notificationType)
    {
        var connection = await CreateConnectionAsync();
        var secret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/seerr-servers/{connection.Id}/webhook")
        {
            Content = JsonContent.Create(new
            {
                notification_type = notificationType,
                media = new { media_type = "movie", tmdbId = "603", media_id = "1" }
            })
        };
        request.Headers.TryAddWithoutValidation("Authorization", secret);

        var response = await _client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Empty(_acquisitions.Calls);
    }

    [Fact]
    public async Task Regenerating_the_secret_invalidates_the_old_one()
    {
        var connection = await CreateConnectionAsync();
        var firstSecret = await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);
        await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None);

        var response = await _client.SendAsync(ApprovedPayload(connection.Id, firstSecret));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

        public Task<int?> FindMediaIdAsync(SeerrConnection connection, string mediaType, string? tmdbId, string? tvdbId, CancellationToken cancellationToken)
            => Task.FromResult<int?>(null);
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
