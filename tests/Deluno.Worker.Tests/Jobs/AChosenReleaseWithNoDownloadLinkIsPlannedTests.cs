using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Worker.Jobs;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>A release with no download link is never sent to a client: it is recorded as planned with a plain message and a typed failure. Refs #1296.</summary>
public sealed class AChosenReleaseWithNoDownloadLinkIsPlannedTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_chosen_release_with_no_download_link_is_planned_with_a_typed_failure_and_is_never_sent(string? url)
    {
        var client = new Mock<IDownloadClientGrabService>(MockBehavior.Strict);
        var candidate = new MediaSearchCandidate("Movie.2024.1080p.WEB-DL-GRP", "ix-1", "Indexer", "WEB 1080p", 50, true, "ok", DownloadUrl: url);

        var result = await SearchExecutionSupport.GrabBestCandidateAsync(
            client.Object, "client-1", candidate,
            new DownloadClientGrabRequest(candidate.ReleaseName, url ?? "", "movies", "deluno-movies", "Indexer"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("planned", result.Status);
        Assert.Equal("No download URL was available.", result.Message);
        Assert.Equal("client-1", result.ClientId);
        Assert.Equal("Movie.2024.1080p.WEB-DL-GRP", result.ReleaseName);
        Assert.NotNull(result.Failure);
        Assert.Equal("grab", result.Failure!.Operation);
        Assert.Equal("download-client", result.Failure.ServiceType);
        Assert.Equal("client-1", result.Failure.ServiceId);
        Assert.Equal("No downloadable URL was available for this release.", result.Failure.Message);
        client.VerifyNoOtherCalls();
    }
}
