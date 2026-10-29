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


/// <summary>
/// Low-severity gap rows (#1329) of the inbound Seerr webhook, against the body Seerr 3.5.0 itself sent when it approved a request
/// (tests/fixtures/recorded/seerr): only the notification type, or what the acquisition answers, differs.
/// </summary>
public sealed class SeerrWebhookLowGapTests : IAsyncDisposable
{
    private readonly TestStorage _storage;
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly SqliteSeerrRepository _repository;
    private readonly AnsweringAcquisitionService _acquisitions = new();

    public SeerrWebhookLowGapTests()
    {
        _storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(_storage.Factory, timeProvider);
        new PlatformSchemaInitializer(_storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _repository = new SqliteSeerrRepository(_storage.Factory, timeProvider, TestSecretProtection.Create(_storage));

        _host = new HostBuilder().ConfigureWebHost(webBuilder =>
        {
            webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<TimeProvider>(timeProvider);
                    services.AddSingleton<ISeerrRepository>(_repository);
                    services.AddSingleton<ISeerrClient>(new NoopSeerrClient());
                    services.AddSingleton<ISeerrAcquisitionsRepository>(new SqliteSeerrAcquisitionsRepository(_storage.Factory, timeProvider));
                    services.AddSingleton<ISeerrAcquisitionService>(_acquisitions);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapDelunoSeerr());
                });
        }).Build();
        _host.StartAsync().GetAwaiter().GetResult();
        _client = _host.GetTestServer().CreateClient();
    }

    private async Task<(string Id, string Secret)> ConnectionWithSecretAsync()
    {
        var connection = (await _repository.SaveAsync(null, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-1", IsEnabled: true), CancellationToken.None))!;
        return (connection.Id, (await _repository.RegenerateWebhookSecretAsync(connection.Id, CancellationToken.None))!);
    }

    private static string Approved() => RecordedReplies.Seerr("webhook-approved").Exchanges[0].Request.Body!;

    private static HttpRequestMessage Posted(string id, string secret, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/seerr-servers/{id}/webhook")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", secret);
        return request;
    }

    private static string WithMediaId(string body)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(body)!;
        json["media"]!["media_id"] = "1";
        return json.ToJsonString();
    }

    [Theory]
    [InlineData("MEDIA_AVAILABLE")]
    [InlineData("MEDIA_FAILED")]
    [InlineData("MEDIA_AUTO_REQUESTED")]
    [InlineData("ISSUE_CREATED")]
    [InlineData("ISSUE_COMMENT")]
    [InlineData("ISSUE_RESOLVED")]
    [InlineData("ISSUE_REOPENED")]
    [InlineData("")]
    public async Task Any_other_notification_type_is_acknowledged_and_never_turned_into_an_add(string notificationType)
    {
        var (id, secret) = await ConnectionWithSecretAsync();
        var json = System.Text.Json.Nodes.JsonNode.Parse(WithMediaId(Approved()))!.AsObject();
        json["notification_type"] = notificationType;

        var response = await _client.SendAsync(Posted(id, secret, json.ToJsonString()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_acquisitions.Calls);
    }

    [Fact]
    public async Task A_body_with_no_notification_type_at_all_is_acknowledged_and_never_turned_into_an_add()
    {
        var (id, secret) = await ConnectionWithSecretAsync();
        var json = System.Text.Json.Nodes.JsonNode.Parse(WithMediaId(Approved()))!.AsObject();
        json.Remove("notification_type");

        var response = await _client.SendAsync(Posted(id, secret, json.ToJsonString()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_acquisitions.Calls);
    }

    [Theory]
    [InlineData(true, "Added Sintel to Movies.")]
    [InlineData(false, "No library takes this title yet.")]
    public async Task The_answer_is_200_with_the_acquisitions_ok_and_message_also_when_the_acquisition_failed(bool ok, string message)
    {
        var (id, secret) = await ConnectionWithSecretAsync();
        _acquisitions.Outcome = new SeerrAcquisitionOutcome(ok, message);

        var response = await _client.SendAsync(Posted(id, secret, WithMediaId(Approved())));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(ok, answer.GetProperty("ok").GetBoolean());
        Assert.Equal(message, answer.GetProperty("message").GetString());
        Assert.Single(_acquisitions.Calls);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _storage.Dispose();
    }

    private sealed class AnsweringAcquisitionService : ISeerrAcquisitionService
    {
        public List<string> Calls { get; } = [];

        public SeerrAcquisitionOutcome Outcome { get; set; } = new(true, "added");

        public Task<SeerrAcquisitionOutcome> AcquireAsync(SeerrConnection connection, string mediaType, string? tmdbId, string? tvdbId, int seerrMediaId, bool is4k, string? requestedBy, CancellationToken cancellationToken)
        {
            Calls.Add($"{mediaType}:{tmdbId}");
            return Task.FromResult(Outcome);
        }
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
}
