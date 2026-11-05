using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// A local setup problem (#880) is shown with the client's own words when it gave any, and with the queue status when it
/// gave none, and it never earns a strike either way.
/// </summary>
public sealed class ASetupProblemWithNoClientMessageNamesTheQueueStatusTests
{
    private static RecoveryQueueSnapshot Item(string? errorMessage)
        => new("error", errorMessage, null, 0, DateTimeOffset.UnixEpoch, 0, "Dune.2021.1080p.WEB-DL-DELUNO", IsSetupProblem: true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_setup_problem_with_no_message_names_the_queue_status(string? message)
    {
        var finding = Assert.Single(
            new DownloadHealthEvaluator().Evaluate(Item(message), DateTimeOffset.UnixEpoch.AddHours(1)),
            item => item.Kind == DownloadHealthStrikes.ClientSetupProblem);

        Assert.Equal("Queue status: error.", finding.Evidence);
        Assert.Equal("critical", finding.Severity);
        Assert.True(finding.CanSafelyRetry);
        Assert.False(finding.CanSafelyRemove);
    }

    [Fact]
    public void A_setup_problem_with_a_message_shows_the_clients_own_words()
    {
        var finding = Assert.Single(
            new DownloadHealthEvaluator().Evaluate(Item("No space left on device"), DateTimeOffset.UnixEpoch.AddHours(1)),
            item => item.Kind == DownloadHealthStrikes.ClientSetupProblem);

        Assert.Equal("No space left on device", finding.Evidence);
    }
}
