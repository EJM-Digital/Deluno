using System.Net;
using System.Text;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Integrations.Tests.Metadata;

/// <summary>
/// #1090: a slow or failing metadata gateway is shown honestly, times out,
/// recovers on Check now and is paced to its real limit. Every test talks to a
/// fake gateway that is slow, down, answers 429 or answers 5xx.
/// </summary>
public sealed class AFailingMetadataGatewayIsSaidHonestlyTests : IDisposable
{
    private const string BrokerUrl = "https://metadata.deluno.test";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-metadata-outage-tests", Guid.NewGuid().ToString("N"));

    public AFailingMetadataGatewayIsSaidHonestlyTests() => Directory.CreateDirectory(_dataRoot);

    [Fact]
    public async Task A_gateway_that_answers_503_is_not_read_as_nothing_found()
    {
        var rig = await RigAsync(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var scope = rig.Provider.BeginFailureScope();
        var results = await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);

        Assert.Empty(results);
        var failure = Assert.IsType<IntegrationFailure>(scope.Failure);
        Assert.True(failure.IsTransient);
        Assert.Equal(503, failure.HttpStatus);
    }

    [Fact]
    public async Task A_search_the_gateway_answered_with_nothing_is_not_a_failure()
    {
        var rig = await RigAsync(_ => Json("""{"provider":"deluno-broker","mode":"broker","resultCount":0,"results":[]}"""));

        using var scope = rig.Provider.BeginFailureScope();
        var results = await rig.Provider.SearchAsync(Lookup("Zzzzqqq"), CancellationToken.None);

        Assert.Empty(results);
        Assert.Null(scope.Failure);
        Assert.Null(rig.Provider.LastFailure);
    }

    [Fact]
    public async Task The_last_failure_is_kept_for_the_whole_process_not_on_one_client()
    {
        var health = new MetadataServiceHealth();
        var down = await RigAsync(_ => new HttpResponseMessage(HttpStatusCode.BadGateway), health);

        await down.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);

        // The typed client is rebuilt for nearly every request: a second
        // instance must still report what the first one saw.
        var another = await RigAsync(_ => new HttpResponseMessage(HttpStatusCode.OK), health);
        var status = await another.Provider.GetStatusAsync(CancellationToken.None);

        Assert.NotNull(status.LastFailure);
        Assert.True(status.IsConfigured);
        Assert.Equal("The metadata service isn't answering; try again in a minute.", status.Message);
        Assert.DoesNotContain("ready", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_status_says_ready_again_once_the_gateway_answers()
    {
        var health = new MetadataServiceHealth();
        var calls = 0;
        var rig = await RigAsync(
            _ => ++calls <= 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json("""{"provider":"deluno-broker","mode":"broker","resultCount":0,"results":[]}"""),
            health);

        await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);
        Assert.NotNull((await rig.Provider.GetStatusAsync(CancellationToken.None)).LastFailure);

        await rig.Provider.SearchAsync(Lookup("Inception 2"), CancellationToken.None);

        var status = await rig.Provider.GetStatusAsync(CancellationToken.None);
        Assert.Null(status.LastFailure);
        Assert.Contains("ready", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_slow_gateway_times_out_instead_of_holding_the_call()
    {
        var health = new MetadataServiceHealth { CallTimeout = TimeSpan.FromMilliseconds(150) };
        var rig = await RigAsync(_ => throw new InvalidOperationException("handled below"), health, slow: true);

        using var scope = rig.Provider.BeginFailureScope();
        var started = DateTimeOffset.UtcNow;
        var results = await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);
        var took = DateTimeOffset.UtcNow - started;

        Assert.Empty(results);
        Assert.True(took < TimeSpan.FromSeconds(10), $"took {took}");
        var failure = Assert.IsType<IntegrationFailure>(scope.Failure);
        Assert.Equal(IntegrationFailureKind.Timeout, failure.Kind);
        Assert.Equal(IntegrationFailureKind.Timeout, rig.Provider.LastFailure?.Kind);
    }

    [Fact]
    public async Task The_gateways_ten_second_limit_is_the_default_call_timeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), new MetadataServiceHealth().CallTimeout);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_429_with_a_long_retry_after_is_waited_out_not_hammered_and_does_not_open_the_circuit()
    {
        var health = new MetadataServiceHealth();
        var gatewayCalls = 0;
        var rig = await RigAsync(_ =>
        {
            gatewayCalls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return response;
        }, health);

        using var first = rig.Provider.BeginFailureScope();
        await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);
        Assert.Equal(1, gatewayCalls);
        Assert.Equal(IntegrationFailureKind.RateLimit, first.Failure?.Kind);

        // A second and third call inside the pause never reach the gateway.
        await rig.Provider.SearchAsync(Lookup("Inception 2"), CancellationToken.None);
        await rig.Provider.SearchAsync(Lookup("Inception 3"), CancellationToken.None);
        Assert.Equal(1, gatewayCalls);

        // Asking for time is not the service being broken.
        Assert.False(rig.Policy.IsCircuitOpen(CircuitKey, out _));
        Assert.True(health.PausedFor("metadata.deluno.test") > TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_short_retry_after_is_honoured_and_the_second_try_goes_through()
    {
        var calls = 0;
        var rig = await RigAsync(_ =>
        {
            if (++calls == 1)
            {
                var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return busy;
            }

            return Json("""{"provider":"deluno-broker","mode":"broker","resultCount":1,"results":[{"provider":"tmdb","providerId":"27205","mediaType":"movies","title":"Inception","year":2010,"genres":[]}]}""");
        });

        var started = DateTimeOffset.UtcNow;
        var results = await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);

        Assert.Equal("Inception", Assert.Single(results).Title);
        Assert.Equal(2, calls);
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(900), "the second try waited for Retry-After");
    }

    [Fact]
    public async Task The_rate_limit_headers_a_gateway_sends_are_honoured_too()
    {
        var health = new MetadataServiceHealth();
        var calls = 0;
        var rig = await RigAsync(_ =>
        {
            calls++;
            var response = Json("""{"provider":"deluno-broker","mode":"broker","resultCount":0,"results":[]}""");
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "40");
            return response;
        }, health);

        await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);
        await rig.Provider.SearchAsync(Lookup("Inception 2"), CancellationToken.None);

        // The first answer said the budget was spent; the second call waited
        // for the window instead of spending a request on a 429.
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Calls_to_the_gateway_are_paced_to_its_real_limit_and_tmdb_is_not()
    {
        var throttle = new RecordingThrottle();
        var rig = await RigAsync(_ => Json("""{"provider":"deluno-broker","mode":"broker","resultCount":0,"results":[]}"""), throttle: throttle);
        await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);

        Assert.Equal(OutboundRate.MetadataGatewayDefault, Assert.Single(throttle.Rates));

        // 120 provider calls a minute per address: never more than 120 in any
        // minute, even across a window boundary.
        var rate = OutboundRate.MetadataGatewayDefault;
        Assert.Equal(TimeSpan.FromMinutes(1), rate.Per);
        Assert.True(rate.Burst + rate.Permits <= 120);
    }

    [Fact]
    public async Task In_broker_mode_an_older_answer_is_used_when_the_gateway_is_down()
    {
        var time = new MovableTimeProvider(DateTimeOffset.Parse("2026-10-01T10:00:00Z"));
        var up = true;
        var rig = await RigAsync(
            _ => up
                ? Json("""{"provider":"deluno-broker","mode":"broker","resultCount":1,"results":[{"provider":"tmdb","providerId":"27205","mediaType":"movies","title":"Inception","year":2010,"genres":[]}]}""")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            time: time);

        Assert.Single(await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None));

        time.Advance(TimeSpan.FromDays(30));
        up = false;
        var stale = await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);

        Assert.Equal("Inception", Assert.Single(stale).Title);
    }

    [Fact]
    public async Task Clearing_the_trouble_forgets_the_failure_and_closes_the_circuits()
    {
        var health = new MetadataServiceHealth();
        var rig = await RigAsync(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), health);

        // Two failed calls open the metadata circuit.
        await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);
        await rig.Provider.SearchAsync(Lookup("Inception 2"), CancellationToken.None);
        Assert.NotNull(rig.Provider.LastFailure);
        Assert.True(rig.Policy.IsCircuitOpen(CircuitKey, out _));

        await rig.Provider.ClearServiceTroubleAsync(CancellationToken.None);

        Assert.Null(rig.Provider.LastFailure);
        Assert.False(rig.Policy.IsCircuitOpen(CircuitKey, out _));
    }

    [Fact]
    public async Task Saving_the_metadata_settings_closes_the_circuits_too()
    {
        var health = new MetadataServiceHealth();
        var rig = await RigAsync(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), health);
        await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);
        await rig.Provider.SearchAsync(Lookup("Inception 2"), CancellationToken.None);
        Assert.True(rig.Policy.IsCircuitOpen(CircuitKey, out _));

        await new MetadataServiceReset(health, rig.Policy).ResetAsync(CancellationToken.None);

        Assert.False(rig.Policy.IsCircuitOpen(CircuitKey, out _));
        Assert.Null(health.LastFailure);
    }

    [Fact]
    public async Task One_title_that_is_not_found_does_not_make_the_service_look_down()
    {
        var rig = await RigAsync(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using var scope = rig.Provider.BeginFailureScope();
        await rig.Provider.SearchAsync(Lookup("Inception"), CancellationToken.None);

        Assert.Null(scope.Failure);
        Assert.Null((await rig.Provider.GetStatusAsync(CancellationToken.None)).LastFailure);
    }

    private const string CircuitKey = "metadata:broker:https://metadata.deluno.test:443";

    private static MetadataLookupRequest Lookup(string title) => new(title, "movies", null, null);

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Rig(TmdbMetadataProvider Provider, IntegrationResiliencePolicy Policy);

    private async Task<Rig> RigAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        MetadataServiceHealth? health = null,
        bool slow = false,
        IOutboundRequestThrottle? throttle = null,
        TimeProvider? time = null)
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformSettingsSnapshot(
                MovieRootPath: null, SeriesRootPath: null, DownloadsPath: null, IncompleteDownloadsPath: null,
                AutoStartJobs: true, EnableNotifications: true, RenameOnImport: true, UseHardlinks: false,
                CleanupEmptyFolders: true,
                MovieFolderFormat: "{Movie Title} ({Release Year})",
                SeriesFolderFormat: "{Series Title} ({Series Year})",
                MovieFileFormat: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
                EpisodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
                HostBindAddress: "127.0.0.1", HostPort: 5099, UrlBase: string.Empty,
                RequireAuthentication: true, UiTheme: "system", DefaultMovieView: "grid", DefaultShowView: "grid",
                MetadataCertificationCountry: "US", MetadataLanguage: "en", MetadataProviderMode: "broker",
                MetadataBrokerUrl: BrokerUrl, MetadataBrokerConfigured: true,
                MetadataTmdbApiKeyConfigured: false, MetadataOmdbApiKeyConfigured: false,
                ReleaseNeverGrabPatterns: string.Empty, SearchScoringMode: SearchScoringModes.Hybrid,
                ImportRecoveryRetentionDays: 30, UpdatedUtc: DateTimeOffset.UtcNow));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var clock = time ?? TimeProvider.System;
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, clock),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var policy = new IntegrationResiliencePolicy(clock, factory, NullLogger<IntegrationResiliencePolicy>.Instance);
        HttpMessageHandler handler = slow ? new HangingHandler() : new StubHandler(respond);
        var provider = new TmdbMetadataProvider(
            new HttpClient(handler),
            new ConfigurationBuilder().Build(),
            settings.Object,
            factory,
            storageOptions,
            clock,
            policy,
            throttle ?? new OutboundRequestThrottle(clock, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance,
            health ?? new MetadataServiceHealth(clock));
        return new Rig(provider, policy);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    /// <summary>A gateway that accepts the connection and never answers.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken); // reason: a gateway that never answers until cancelled
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class RecordingThrottle : IOutboundRequestThrottle
    {
        public List<OutboundRate> Rates { get; } = [];

        public ValueTask<TimeSpan?> TryAcquireAsync(string host, OutboundRate rate, TimeSpan maxWait, CancellationToken cancellationToken)
        {
            Rates.Add(rate);
            return ValueTask.FromResult<TimeSpan?>(TimeSpan.Zero);
        }

        public IReadOnlyList<OutboundThrottleHostState> Describe() => [];
    }

    private sealed class MovableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    public void Dispose()
    {
        new SqliteDatabaseConnectionFactory(Options.Create(new StoragePathOptions { DataRoot = _dataRoot })).ClearPools();
        try
        {
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup for test temp dirs
        }
    }
}
