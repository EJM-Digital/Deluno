using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.Processors;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #1055: on a clean VM the health board said "Transmission: Not tested yet" 18 seconds
/// after Transmission's connection had tested healthy. The installer's connecting pass adds
/// a client and then tests it (up to ten tries); the health check read the client in that
/// gap. A client or processor that was just added and has never been tested is left alone;
/// one that stays untested past the grace is still reported.
/// </summary>
public sealed class NewConnectionGraceHealthCheckTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T07:48:20Z");

    [Fact]
    public async Task A_download_client_added_seconds_ago_and_not_yet_tested_is_not_reported()
    {
        var outcomes = await RunClientCheckAsync(Client(createdUtc: Now.AddSeconds(-8), tested: null, status: "untested"));

        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task A_download_client_that_stays_untested_past_the_grace_is_reported()
    {
        var outcomes = await RunClientCheckAsync(Client(createdUtc: Now - DownloadClientHealthCheck.NewClientGrace - TimeSpan.FromMinutes(1), tested: null, status: "untested"));

        Assert.Equal(HealthCheckStatuses.Problem, Assert.Single(outcomes).Status);
    }

    [Fact]
    public async Task A_new_download_client_that_was_tested_and_failed_is_reported_at_once()
    {
        var outcomes = await RunClientCheckAsync(Client(createdUtc: Now.AddSeconds(-8), tested: Now.AddSeconds(-2), status: "unreachable"));

        Assert.Equal(HealthCheckStatuses.Problem, Assert.Single(outcomes).Status);
    }

    [Fact]
    public async Task A_usenet_client_with_no_provider_says_so_in_plain_words_not_that_it_is_not_answering()
    {
        var client = Client(createdUtc: Now.AddHours(-1), tested: Now.AddMinutes(-1), status: "degraded") with
        {
            Name = "SABnzbd",
            LastHealthMessage = Deluno.Connections.Usenet.UsenetProviderText.NoProvider
        };

        var outcome = Assert.Single(await RunClientCheckAsync(client));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("SABnzbd has no Usenet provider set up.", outcome.Message);
        Assert.DoesNotContain("not answering", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Usenet provider", outcome.Fix);
    }

    [Fact]
    public async Task A_processor_added_seconds_ago_and_not_yet_tested_is_not_reported()
    {
        var connection = new ProcessorConnectionItem("p1", "Weir", "generic-webhook", "http://127.0.0.1:9347/hook", "X-Webhook-Secret", null, true, "unknown", null, null, Now.AddSeconds(-8), Now.AddSeconds(-8));
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([connection]);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var outcomes = await new ProcessorHealthCheck(processors.Object, new Mock<IProcessorConnectionService>().Object, libraries.Object, new FixedClock(Now)).RunAsync(CancellationToken.None);

        Assert.Empty(outcomes);
    }

    private static async Task<IReadOnlyList<HealthCheckOutcome>> RunClientCheckAsync(DownloadClientItem client)
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([client]);
        return await new DownloadClientHealthCheck(connections.Object, new FixedClock(Now)).RunAsync(CancellationToken.None);
    }

    private static DownloadClientItem Client(DateTimeOffset createdUtc, DateTimeOffset? tested, string status)
        => new(
            "c1", "Transmission", "transmission", "localhost", 9091, null, null, null, false, null,
            "deluno-movies", "deluno-tv", null, 1, true, status, status == "untested" ? "Not tested yet." : "Did not answer.", null, null, tested, createdUtc, createdUtc);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
