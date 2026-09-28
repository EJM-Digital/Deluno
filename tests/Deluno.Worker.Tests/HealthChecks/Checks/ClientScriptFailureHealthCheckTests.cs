using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #952: a client's own post-processing script failing or warning is shown
/// on System &#8594; Health as well as in Activity, naming the script the
/// client reported. Distinct from a stalled or dead download -- never counts
/// toward a strike, never blocklists or searches for a replacement.
/// </summary>
public sealed class ClientScriptFailureHealthCheckTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1);

    [Fact]
    public async Task No_script_findings_reports_nothing()
    {
        var health = HealthReturning(
            new DownloadHealthRecord("client-1", "q1", "Sintel", "sintel-key", DownloadHealthStrikes.ClientStalled,
                "critical", "No progress for 30 minutes.", Now, Now, 1, null));
        var check = new ClientScriptFailureHealthCheck(health, ConnectionsReturning());

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task A_failed_client_script_is_shown_on_health_naming_the_script()
    {
        var health = HealthReturning(
            new DownloadHealthRecord("client-1", "q1", "Big Buck Bunny", "bbb-key", DownloadHealthStrikes.ClientScriptFailed,
                "critical", "Your client's script \"extract.sh\" failed.", Now, Now, 1, null));
        var client = Client("client-1", "My NZBGet");
        var check = new ClientScriptFailureHealthCheck(health, ConnectionsReturning(client));

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal(HealthCheckCategories.DownloadClient, outcome.Category);
        Assert.Equal("client-script-failed:client-1:q1", outcome.Id);
        Assert.Contains("Big Buck Bunny", outcome.Message);
        Assert.Contains("extract.sh", outcome.Message);
        Assert.Contains("My NZBGet", outcome.Cause);
        Assert.Contains("My NZBGet", outcome.Fix);
    }

    [Fact]
    public async Task A_warned_client_script_is_still_reported_as_a_problem()
    {
        var health = HealthReturning(
            new DownloadHealthRecord("client-1", "q1", "Sintel", "sintel-key", DownloadHealthStrikes.ClientScriptFailed,
                "warning", "Your client's script \"cleanup.sh\" warned.", Now, Now, 1, null));
        var check = new ClientScriptFailureHealthCheck(health, ConnectionsReturning(Client("client-1", "SABnzbd")));

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Contains("cleanup.sh", outcome.Message);
    }

    [Fact]
    public async Task Clears_once_the_client_stops_reporting_the_finding()
    {
        // RecordDownloadHealthObservationsAsync ends the record the moment the
        // client lists the download again without the finding (the script ran
        // cleanly), so the repository simply stops returning it -- this check
        // then reports nothing for that id, and HealthCheckRunner prunes the
        // stale row for a category it just ran (#914-style pruning).
        var health = HealthReturning();
        var check = new ClientScriptFailureHealthCheck(health, ConnectionsReturning(Client("client-1", "My NZBGet")));

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.Empty(outcomes);
    }

    private static IDownloadHealthRepository HealthReturning(params DownloadHealthRecord[] records)
    {
        var repository = new Mock<IDownloadHealthRepository>();
        repository.Setup(instance => instance.ListDownloadHealthRecordsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(records);
        return repository.Object;
    }

    private static IConnectionsRepository ConnectionsReturning(params DownloadClientItem[] clients)
    {
        var repository = new Mock<IConnectionsRepository>();
        repository.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(clients);
        return repository.Object;
    }

    private static DownloadClientItem Client(string id, string name)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new DownloadClientItem(
            Id: id,
            Name: name,
            Protocol: "usenet",
            Host: "localhost",
            Port: 6789,
            Username: null,
            Secret: null,
            EndpointUrl: null,
            UseSsl: false,
            UrlBase: null,
            MoviesCategory: "movies",
            TvCategory: null,
            CategoryTemplate: null,
            Priority: 1,
            IsEnabled: true,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: now,
            CreatedUtc: now,
            UpdatedUtc: now);
    }
}
