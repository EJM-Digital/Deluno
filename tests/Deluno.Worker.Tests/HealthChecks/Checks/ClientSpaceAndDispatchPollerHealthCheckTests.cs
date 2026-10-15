using System.Runtime.CompilerServices;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// Two System > Health rows (#1294): each download client's own folder space, one row per client, and the dispatch-recovery poller
/// that shows a problem while its circuit breaker is open.
/// </summary>
public sealed class ClientSpaceAndDispatchPollerHealthCheckTests
{
    private static DownloadClientTelemetrySnapshot Snapshot(string name, string protocol, long? freeBytes, string? healthMessage = null)
        => ((DownloadClientTelemetrySnapshot)RuntimeHelpers.GetUninitializedObject(typeof(DownloadClientTelemetrySnapshot))) with
        {
            ClientId = name.ToLowerInvariant().Replace(' ', '-'), ClientName = name, Protocol = protocol, FreeSpaceBytes = freeBytes, LastHealthMessage = healthMessage
        };

    private static ClientDownloadFolderFreeSpaceHealthCheck CheckFor(params DownloadClientTelemetrySnapshot[] clients)
    {
        var overview = ((DownloadTelemetryOverview)RuntimeHelpers.GetUninitializedObject(typeof(DownloadTelemetryOverview))) with { Clients = clients };
        var telemetry = new Mock<IDownloadClientTelemetryService>();
        telemetry.Setup(service => service.GetLatestOverviewAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(overview);
        return new ClientDownloadFolderFreeSpaceHealthCheck(telemetry.Object);
    }

    [Fact]
    public async Task Each_client_gets_its_own_row_and_only_the_one_low_on_space_is_a_problem()
    {
        var outcomes = await CheckFor(
            Snapshot("qBit Low", "qbittorrent", 1_000_000_000, "Only 1 GB free — downloads will start failing."),
            Snapshot("qBit Fine", "qbittorrent", 500_000_000_000, null),
            Snapshot("SAB Fine", "sabnzbd", 250_000_000_000, null)).RunAsync(CancellationToken.None);

        Assert.Equal(3, outcomes.Count);
        Assert.Equal(3, outcomes.Select(outcome => outcome.Id).Distinct().Count());
        var problem = Assert.Single(outcomes, outcome => outcome.Status == HealthCheckStatuses.Problem);
        Assert.Equal("download-client:qbit-low:free-space", problem.Id);
        Assert.Contains("qBit Low", problem.Message);
        Assert.Contains("Only 1 GB free", problem.Message);
        Assert.All(outcomes.Where(outcome => outcome.Id != problem.Id), outcome => Assert.Equal(HealthCheckStatuses.Ok, outcome.Status));
        Assert.Contains("500 GB free", outcomes.Single(outcome => outcome.Id == "download-client:qbit-fine:free-space").Message);
    }

    [Fact]
    public async Task A_client_that_could_not_be_read_is_unknown_not_a_problem_and_not_fine()
    {
        var outcomes = await CheckFor(Snapshot("qBit", "qbittorrent", freeBytes: null)).RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Contains("could not read free space from qBit", outcome.Message);
    }

    [Fact]
    public async Task A_client_whose_api_has_no_free_space_call_says_exactly_that()
    {
        var outcome = Assert.Single(await CheckFor(Snapshot("uTorrent", "utorrent", freeBytes: null)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Contains("has no call for it", outcome.Message);
    }

    [Fact]
    public async Task No_clients_means_no_rows()
    {
        Assert.Empty(await CheckFor().RunAsync(CancellationToken.None));
    }

    private sealed class Breaker(bool open, int failures, DateTimeOffset? resumes) : IDownloadDispatchCircuitBreakerState
    {
        public bool IsOpen => open;
        public DateTimeOffset? ResumesAtUtc => resumes;
        public int ConsecutiveFailures => failures;
    }

    [Fact]
    public async Task The_poller_shows_a_problem_while_its_circuit_breaker_is_open_with_when_it_resumes()
    {
        var resumes = new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.Zero);

        var outcome = Assert.Single(await new DispatchRecoveryPollerHealthCheck(new Breaker(open: true, failures: 3, resumes)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("system:dispatch-recovery-poller", outcome.Id);
        Assert.Contains("after 3 failed passes in a row", outcome.Message);
        Assert.Contains($"{resumes:u}", outcome.Message);
        Assert.Contains("resumes on its own", outcome.Fix);
    }

    [Fact]
    public async Task The_poller_reads_fine_while_its_circuit_breaker_is_closed()
    {
        var outcome = Assert.Single(await new DispatchRecoveryPollerHealthCheck(new Breaker(open: false, failures: 0, null)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("Running on its normal schedule.", outcome.Message);
    }
}
