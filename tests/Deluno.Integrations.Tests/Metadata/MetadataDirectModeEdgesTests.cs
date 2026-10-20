using System.Net;
using System.Net.Http.Headers;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Integrations.Tests.Metadata;

/// <summary>
/// The metadata provider asking TMDb itself (a key of the person's own, no managed gateway): a rate limit answered with a status line and a
/// Retry-After (TMDb's own body is not involved: the status is what Deluno classifies), the reachability probe, and the hybrid mode that
/// falls back to TMDb only when the gateway cannot answer. The gateway's side of the hybrid is the gateway's recorded search
/// (tests/fixtures/recorded/tmdb).
/// </summary>
public sealed class MetadataDirectModeEdgesTests : IDisposable
{
    private const string BrokerUrl = "https://metadata.deluno.test";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-metadata-direct-edges-tests", Guid.NewGuid().ToString("N"));

    public MetadataDirectModeEdgesTests() => Directory.CreateDirectory(_dataRoot);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private async Task<(TmdbMetadataProvider Provider, IntegrationResiliencePolicy Policy)> RigAsync(string mode, Func<HttpRequestMessage, HttpResponseMessage> respond)
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
                MetadataCertificationCountry: "US", MetadataLanguage: "en", MetadataProviderMode: mode,
                MetadataBrokerUrl: mode == "direct" ? string.Empty : BrokerUrl, MetadataBrokerConfigured: mode != "direct",
                MetadataTmdbApiKeyConfigured: true, MetadataOmdbApiKeyConfigured: false,
                ReleaseNeverGrabPatterns: string.Empty, SearchScoringMode: SearchScoringModes.Hybrid,
                ImportRecoveryRetentionDays: 30, UpdatedUtc: DateTimeOffset.UtcNow));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var clock = TimeProvider.System;
        await new CacheSchemaInitializer(factory, new SqliteDatabaseMigrator(factory, clock), NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var policy = new IntegrationResiliencePolicy(clock, factory, NullLogger<IntegrationResiliencePolicy>.Instance);
        var provider = new TmdbMetadataProvider(
            new HttpClient(new StubHandler(respond)),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Deluno:Metadata:TMDbApiKey"] = "test-key" }).Build(),
            settings.Object,
            factory,
            storageOptions,
            clock,
            policy,
            new OutboundRequestThrottle(clock, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance,
            new MetadataServiceHealth(clock));
        return (provider, policy);
    }

    private static HttpResponseMessage Busy(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    [Fact]
    public async Task A_direct_TMDb_429_with_a_long_Retry_After_is_a_rate_limit_not_nothing_found_and_is_not_asked_again_inside_the_pause()
    {
        var calls = 0;
        var (provider, policy) = await RigAsync("direct", _ =>
        {
            calls++;
            return Busy(TimeSpan.FromSeconds(45));
        });

        using var scope = provider.BeginFailureScope();
        var first = await provider.SearchAsync(new MetadataLookupRequest("Inception", "movies", null, null), CancellationToken.None);
        var second = await provider.SearchAsync(new MetadataLookupRequest("Inception 2", "movies", null, null), CancellationToken.None);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Equal(IntegrationFailureKind.RateLimit, scope.Failure?.Kind);
        Assert.Equal(1, calls);
        Assert.False(policy.IsCircuitOpen("metadata:tmdb:search", out _) || policy.IsCircuitOpen("metadata", out _));
    }

    [Fact]
    public async Task A_direct_TMDb_429_with_a_short_Retry_After_is_waited_for_and_the_second_try_gets_the_answer()
    {
        var calls = 0;
        var (provider, _) = await RigAsync("direct", request =>
        {
            if (++calls == 1)
            {
                return Busy(TimeSpan.FromSeconds(1));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"page":1,"results":[{"id":27205,"title":"Inception","release_date":"2010-07-15","overview":"A thief."}],"total_pages":1,"total_results":1}""", System.Text.Encoding.UTF8, "application/json")
            };
        });
        var started = DateTimeOffset.UtcNow;

        var results = await provider.SearchAsync(new MetadataLookupRequest("Inception", "movies", null, null), CancellationToken.None);

        Assert.Equal("Inception", Assert.Single(results).Title);
        Assert.True(calls >= 2);
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(900), "the second try waited for the Retry-After");
    }

    [Fact]
    public async Task The_direct_reachability_probe_asks_TMDbs_configuration_with_the_key_and_a_refused_key_is_said_in_the_status()
    {
        var asked = new List<string>();
        var answer = HttpStatusCode.OK;
        var (provider, _) = await RigAsync("direct", request =>
        {
            asked.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(answer) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        });

        var ready = await provider.CheckReachabilityAsync(CancellationToken.None);
        answer = HttpStatusCode.Unauthorized;
        var refused = await provider.CheckReachabilityAsync(CancellationToken.None);

        Assert.Equal("direct", ready.Mode);
        Assert.Null(ready.LastFailure);
        Assert.Equal("https://api.themoviedb.org/3/configuration?api_key=test-key", asked[0]);
        Assert.Equal(IntegrationFailureKind.Authentication, refused.LastFailure?.Kind);
        Assert.NotEqual(ready.Message, refused.Message);
    }

    [Fact]
    public async Task In_hybrid_mode_TMDb_is_probed_only_when_the_gateway_did_not_answer()
    {
        var gatewayUp = true;
        var asked = new List<string>();
        var (provider, _) = await RigAsync("hybrid", request =>
        {
            asked.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "api.themoviedb.org")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            }

            return gatewayUp
                ? RecordedReplies.Tmdb("search-movie").Single.Reply.ToResponse()
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        await provider.CheckReachabilityAsync(CancellationToken.None);
        Assert.DoesNotContain("api.themoviedb.org", asked);

        gatewayUp = false;
        asked.Clear();
        await provider.CheckReachabilityAsync(CancellationToken.None);

        Assert.Contains("metadata.deluno.test", asked);
        Assert.Contains("api.themoviedb.org", asked);
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
