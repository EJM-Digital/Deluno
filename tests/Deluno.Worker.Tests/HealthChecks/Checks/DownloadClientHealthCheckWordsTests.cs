using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>#909, #1055, #1198: what System, Health says about each download client, for every state a client can be in.</summary>
public sealed class DownloadClientHealthCheckWordsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T07:48:20Z");

    private static DownloadClientItem Client(string status = "healthy", string? message = null, bool enabled = true, DateTimeOffset? tested = null, DateTimeOffset? created = null)
        => new(
            "c1", "Transmission", "transmission", "localhost", 9091, null, null, null, false, null,
            "deluno-movies", "deluno-tv", null, 1, enabled, status, message, null, null, tested ?? Now.AddMinutes(-1), created ?? Now.AddDays(-1), created ?? Now.AddDays(-1));

    private static async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(params DownloadClientItem[] clients)
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(clients);
        return await new DownloadClientHealthCheck(connections.Object, new FixedClock(Now)).RunAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("HEALTHY")]
    public async Task A_healthy_client_is_reachable(string status)
    {
        var outcome = Assert.Single(await RunAsync(Client(status)));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("download-client:c1", outcome.Id);
        Assert.Equal("Transmission (download client)", outcome.Name);
        Assert.Equal("Transmission is reachable.", outcome.Message);
        Assert.Equal(HealthCheckCategories.DownloadClient, new DownloadClientHealthCheck(new Mock<IConnectionsRepository>().Object, TimeProvider.System).Category);
    }

    [Fact]
    public async Task A_client_that_is_switched_off_is_not_reported()
    {
        Assert.Empty(await RunAsync(Client("unreachable", "Did not answer.", enabled: false)));
    }

    [Fact]
    public async Task A_client_that_failed_its_test_says_what_the_client_said_and_what_to_do()
    {
        var outcome = Assert.Single(await RunAsync(Client("unreachable", "Connection refused.")));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Transmission: Connection refused.", outcome.Message);
        Assert.Equal("Deluno could not confirm this download client's last connection test.", outcome.Cause);
        Assert.Equal("Open Connections and test Transmission. Grabs to it are held until it passes.", outcome.Fix);
        Assert.Equal("""{"clientId":"c1","protocol":"transmission"}""", outcome.DetailsJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_client_that_failed_with_no_words_is_not_answering(string? message)
    {
        var outcome = Assert.Single(await RunAsync(Client("unreachable", message)));

        Assert.Equal("Transmission is not answering.", outcome.Message);
    }

    [Fact]
    public async Task A_password_stored_on_another_computer_says_to_enter_it_again()
    {
        var outcome = Assert.Single(await RunAsync(Client("unreachable", "Login failed.") with { SecretUnreadable = true }));

        Assert.Equal("The saved password was stored on another computer, so this computer cannot read it.", outcome.Cause);
        Assert.Equal("Open Transmission in Connections, enter its password again and save. Grabs to it are held until then.", outcome.Fix);
    }

    [Fact]
    public async Task A_usenet_client_with_no_provider_is_still_found_when_the_words_have_spaces_around_them()
    {
        var outcome = Assert.Single(await RunAsync(Client("degraded", "  " + Deluno.Connections.Usenet.UsenetProviderText.NoProvider + "  ")));

        Assert.Equal(DownloadClientHealthCheck.NoProviderMessage("Transmission"), outcome.Message);
        Assert.Equal("Transmission is running, but it has no Usenet provider (news server) to download from.", outcome.Cause);
        Assert.Equal("Add your Usenet provider under Find & Download, Download clients. Deluno writes it to every Usenet client.", outcome.Fix);
        Assert.Equal("""{"clientId":"c1","protocol":"transmission"}""", outcome.DetailsJson);
    }

    [Fact]
    public async Task A_client_with_other_words_is_not_taken_for_one_with_no_provider()
    {
        var outcome = Assert.Single(await RunAsync(Client("degraded", "no usenet provider")));

        Assert.Equal("Transmission: no usenet provider", outcome.Message);
    }

    [Fact]
    public async Task A_new_untested_client_is_left_alone_until_the_grace_runs_out_and_not_a_moment_longer()
    {
        var neverTested = (DateTimeOffset created) => Client("untested", "Not tested yet.", created: created) with { LastHealthTestUtc = null };

        Assert.Empty(await RunAsync(neverTested(Now.AddMinutes(-4))));
        Assert.Single(await RunAsync(neverTested(Now - DownloadClientHealthCheck.NewClientGrace)));
    }

    [Fact]
    public async Task A_new_client_that_was_never_tested_but_is_already_healthy_is_reported_as_reachable()
    {
        var outcome = Assert.Single(await RunAsync(Client("healthy", created: Now.AddSeconds(-5)) with { LastHealthTestUtc = null }));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
    }

    [Fact]
    public async Task A_tested_new_client_is_reported_even_inside_the_grace()
    {
        var outcome = Assert.Single(await RunAsync(Client("unreachable", "Did not answer.", tested: Now.AddSeconds(-2), created: Now.AddSeconds(-8))));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
    }

    [Fact]
    public void The_grace_for_a_new_client_is_five_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), DownloadClientHealthCheck.NewClientGrace);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
