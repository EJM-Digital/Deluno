using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Giving up on a release removes only the download Deluno itself sent, and only then closes the record, so the title is
/// not left "on its way" and the new search is not skipped.
/// </summary>
public sealed class GivenUpDownloadRemovalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00Z");

    private static DownloadDispatchItem Dispatch(string clientId, string? hash) => new(
        "dispatch-1", "lib", "movies", "movie", "m1", "Heat.1995", "Indexer", clientId, "Client", "completed", null, Now,
        null, null, null, null, null, null, null, hash, null, null, null, null, null, null, null, null);

    private static QueueRemovalResult Removal(bool succeeded, string message) => new(
        "client-1", "hash-1", QueueRemovalReasons.GaveUp, succeeded, message, false, false, null, null);

    [Theory]
    [InlineData("", "hash-1")]
    [InlineData("  ", "hash-1")]
    [InlineData("client-1", "")]
    [InlineData("client-1", "   ")]
    [InlineData("client-1", null)]
    public async Task A_dispatch_that_does_not_say_which_download_to_remove_is_refused_and_touches_nothing(string clientId, string? hash)
    {
        var clients = new Mock<IDownloadClientTelemetryService>(MockBehavior.Strict);
        var dispatches = new Mock<IDownloadDispatchesRepository>(MockBehavior.Strict);

        var result = await new GivenUpDownloadRemoval(clients.Object, dispatches.Object).RemoveAsync(Dispatch(clientId, hash), CancellationToken.None);

        Assert.False(result.Removed);
        Assert.Equal("Deluno cannot tell which download in the client to remove.", result.Message);
    }

    [Fact]
    public async Task A_removal_the_client_refuses_is_passed_on_in_its_words_and_the_record_stays_open()
    {
        var clients = new Mock<IDownloadClientTelemetryService>();
        clients.Setup(c => c.RemoveWithReasonAsync("client-1", "hash-1", QueueRemovalReasons.GaveUp, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Removal(false, "Deluno did not send this download."));
        var dispatches = new Mock<IDownloadDispatchesRepository>(MockBehavior.Strict);

        var result = await new GivenUpDownloadRemoval(clients.Object, dispatches.Object).RemoveAsync(Dispatch("client-1", "hash-1"), CancellationToken.None);

        Assert.False(result.Removed);
        Assert.Equal("Deluno did not send this download.", result.Message);
    }

    [Fact]
    public async Task A_removed_download_closes_its_record_with_the_reason_in_plain_words()
    {
        var clients = new Mock<IDownloadClientTelemetryService>();
        clients.Setup(c => c.RemoveWithReasonAsync("client-1", "hash-1", QueueRemovalReasons.GaveUp, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Removal(true, "Removed."));
        var dispatches = new Mock<IDownloadDispatchesRepository>();

        var result = await new GivenUpDownloadRemoval(clients.Object, dispatches.Object).RemoveAsync(Dispatch("client-1", "hash-1"), CancellationToken.None);

        Assert.True(result.Removed);
        Assert.Null(result.Message);
        dispatches.Verify(d => d.ArchiveDispatchAsync("dispatch-1", "Gave up on this release and removed it from the client.", It.IsAny<CancellationToken>()), Times.Once);
    }
}
