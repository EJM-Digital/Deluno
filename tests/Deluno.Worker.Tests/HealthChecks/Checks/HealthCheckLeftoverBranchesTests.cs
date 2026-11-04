using System.Net;
using System.Runtime.CompilerServices;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search.Trackers;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>The last untested branches of five small health checks: a failed daily check with no note, a client folder that names itself, a helper address that is not a web address, a proxy that answers wrongly, and the sleep times in words.</summary>
public sealed class HealthCheckLeftoverBranchesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-leftovers-" + Guid.NewGuid().ToString("N"));

    public HealthCheckLeftoverBranchesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_failed_daily_database_check_with_no_note_still_says_a_database_failed(string? note)
    {
        DatabaseMaintenanceLog.RecordIntegrity(_root, DateTimeOffset.UtcNow, false, note);
        var check = new DatabaseIntegrityHealthCheck(Options.Create(new StoragePathOptions { DataRoot = _root }));

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("A database did not pass its daily check.", outcome.Message);
        Assert.Equal("A database file is damaged. This can follow a power cut, a full drive or a failing disk.", outcome.Cause);
        Assert.Equal("Restore the last backup from System, Backups. If there is none, keep a copy of the data folder and ask for help before changing anything.", outcome.Fix);
    }

    [Fact]
    public async Task A_passed_daily_database_check_says_every_database_passed()
    {
        DatabaseMaintenanceLog.RecordIntegrity(_root, DateTimeOffset.UtcNow, true, null);
        var check = new DatabaseIntegrityHealthCheck(Options.Create(new StoragePathOptions { DataRoot = _root }));

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal("Every database passed its daily check.", outcome.Message);
    }

    [Fact]
    public async Task A_low_space_warning_that_already_names_the_client_is_not_prefixed_with_its_name_again()
    {
        var snapshot = ((DownloadClientTelemetrySnapshot)RuntimeHelpers.GetUninitializedObject(typeof(DownloadClientTelemetrySnapshot))) with
        {
            ClientId = "qbit", ClientName = "qBit", Protocol = "qbittorrent", FreeSpaceBytes = 1_000_000_000,
            LastHealthMessage = "qBit: only 1 GB free — downloads will start failing."
        };
        var overview = ((DownloadTelemetryOverview)RuntimeHelpers.GetUninitializedObject(typeof(DownloadTelemetryOverview))) with { Clients = [snapshot] };
        var telemetry = new Mock<IDownloadClientTelemetryService>();
        telemetry.Setup(service => service.GetLatestOverviewAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(overview);

        var outcome = Assert.Single(await new ClientDownloadFolderFreeSpaceHealthCheck(telemetry.Object).RunAsync(CancellationToken.None));

        Assert.Equal("qBit: only 1 GB free — downloads will start failing.", outcome.Message);
        Assert.Equal("qBit's own download folder is running low on space.", outcome.Cause);
        Assert.Equal("Free up space where qBit downloads to, or move some of its downloads elsewhere.", outcome.Fix);
        Assert.Equal("qBit's download folder space", outcome.Name);
    }

    [Fact]
    public async Task A_helper_address_that_is_not_a_web_address_is_a_problem_naming_what_was_typed()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(instance => instance.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Deluno.Persistence.Tests.Support.FakePlatformSettingsRepository.Default() with { CloudflareHelperUrl = "  not an address  " });
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);

        var outcome = Assert.Single(await new CloudflareHelperHealthCheck(settings.Object, factory.Object).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("The Cloudflare helper address \"not an address\" is not a web address.", outcome.Message);
        Assert.Equal("Trackers behind a Cloudflare check cannot be searched without a working helper.", outcome.Cause);
        Assert.Equal("Fix the helper's address in Preferences, General, such as http://127.0.0.1:8191.", outcome.Fix);
        Assert.Equal(HealthCheckCategories.Indexer, new CloudflareHelperHealthCheck(settings.Object, factory.Object).Category);
    }

    [Theory]
    [InlineData("""{"status":"error"}""")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task A_flaresolverr_proxy_that_does_not_answer_ok_is_not_reachable(string body)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HealthCheckHttpClients.Proxy)).Returns(new HttpClient(new Answering(HttpStatusCode.OK, body)));
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var proxy = new ProxyItem("p1", "Byparr", ProxyTypes.FlareSolverr, "http://127.0.0.1:8191", null, null, true, now, now);

        var result = await new HttpProxyReachabilityTester(factory.Object).TestAsync(proxy, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Equal("Byparr did not answer sessions.list with status ok.", result.Message);
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("ok")]
    public async Task A_flaresolverr_proxy_that_answers_ok_is_reachable_whatever_the_case(string status)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HealthCheckHttpClients.Proxy)).Returns(new HttpClient(new Answering(HttpStatusCode.OK, "{\"status\":\"" + status + "\"}")));
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var proxy = new ProxyItem("p1", "Byparr", ProxyTypes.FlareSolverr, "http://127.0.0.1:8191/v1", null, null, true, now, now);

        var result = await new HttpProxyReachabilityTester(factory.Object).TestAsync(proxy, CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.Equal("Byparr answered its sessions.list command.", result.Message);
    }

    [Theory]
    [InlineData(45, "45 seconds")]
    [InlineData(89, "89 seconds")]
    [InlineData(90, "2 minutes")]
    [InlineData(600, "10 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(7200, "2 hours")]
    [InlineData(5400, "90 minutes")]
    public void Sleep_times_are_said_in_the_biggest_plain_unit(int seconds, string words)
    {
        Assert.Equal(words, ComputerSleepHealthCheck.Minutes(seconds));
    }

    private sealed class Answering(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
