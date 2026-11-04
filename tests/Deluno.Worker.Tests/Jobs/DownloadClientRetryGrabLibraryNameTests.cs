using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>#875: a retry with nothing to resend does nothing, and the library named in a dead-release follow-up is its name when it can be read and its id when it cannot.</summary>
public sealed class DownloadClientRetryGrabLibraryNameTests
{
    private static string Payload() => JsonSerializer.Serialize(new
    {
        DownloadClientId = "client-1", DownloadClientName = "SABnzbd", LibraryId = "library-1", MediaType = "movies", EntityType = "movie",
        EntityId = "movie-1", Title = "Tears of Steel", ReleaseName = "Tears.of.Steel.2012", DownloadUrl = "http://fixture.invalid/x.nzb",
        Category = "movies", IndexerName = "Fixture", AttemptNumber = 1
    });

    private static (DownloadClientRetryGrabJobHandler Handler, List<DownloadClientGrabRequest> Sent) Build(ILibrariesRepository? libraries)
    {
        var sent = new List<DownloadClientGrabRequest>();
        var grabs = new Mock<IDownloadClientGrabService>();
        grabs.Setup(g => g.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, DownloadClientGrabRequest, CancellationToken>((_, request, _) => sent.Add(request))
            .ReturnsAsync(new DownloadClientGrabResult("client-1", "Tears.of.Steel.2012", true, "sent", "Sent.") { ExternalId = "nzo-1" });
        var handler = new DownloadClientRetryGrabJobHandler(
            grabs.Object, new Mock<IJobQueueRepository>().Object, new Mock<IActivityFeedRepository>().Object, new Mock<IJobScheduler>().Object, TimeProvider.System, libraries);
        return (handler, sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("null")]
    public async Task A_retry_with_nothing_to_resend_says_so_and_sends_nothing(string? payload)
    {
        var (handler, sent) = Build(null);

        var message = await handler.HandleAsync(TestJobs.Create(SearchExecutionSupport.DownloadClientRetryGrabJobType, payloadJson: payload), CancellationToken.None);

        Assert.Equal("Connectivity retry skipped: nothing to resend.", message);
        Assert.Empty(sent);
        Assert.Equal(SearchExecutionSupport.DownloadClientRetryGrabJobType, handler.JobType);
    }

    private static LibraryItem Library(string id, string name)
        => ((LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem))) with { Id = id, Name = name };

    [Fact]
    public async Task The_library_is_named_by_its_name_found_in_any_case_of_the_id()
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library("other", "Shows"), Library("LIBRARY-1", "Films")]);
        var (handler, sent) = Build(libraries.Object);

        await handler.HandleAsync(TestJobs.Create(SearchExecutionSupport.DownloadClientRetryGrabJobType, payloadJson: Payload()), CancellationToken.None);

        Assert.Equal("Films", Assert.Single(sent).OnDeadRelease!.LibraryName);
    }

    [Fact]
    public async Task A_library_that_is_no_longer_there_or_cannot_be_read_is_named_by_its_id()
    {
        var gone = new Mock<ILibrariesRepository>();
        gone.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library("other", "Shows")]);
        var broken = new Mock<ILibrariesRepository>();
        broken.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("db busy"));
        var (goneHandler, goneSent) = Build(gone.Object);
        var (brokenHandler, brokenSent) = Build(broken.Object);
        var (noneHandler, noneSent) = Build(null);

        foreach (var handler in new[] { goneHandler, brokenHandler, noneHandler })
        {
            await handler.HandleAsync(TestJobs.Create(SearchExecutionSupport.DownloadClientRetryGrabJobType, payloadJson: Payload()), CancellationToken.None);
        }

        Assert.Equal("library-1", Assert.Single(goneSent).OnDeadRelease!.LibraryName);
        Assert.Equal("library-1", Assert.Single(brokenSent).OnDeadRelease!.LibraryName);
        Assert.Equal("library-1", Assert.Single(noneSent).OnDeadRelease!.LibraryName);
    }

    [Fact]
    public async Task A_stop_while_the_library_is_read_is_not_hidden()
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        var (handler, _) = Build(libraries.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(TestJobs.Create(SearchExecutionSupport.DownloadClientRetryGrabJobType, payloadJson: Payload()), CancellationToken.None));
    }

    [Fact]
    public async Task A_release_from_an_indexer_with_no_name_is_recorded_with_an_empty_indexer_name()
    {
        var jobs = new Mock<IJobQueueRepository>();
        var grabs = new Mock<IDownloadClientGrabService>();
        grabs.Setup(g => g.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientGrabResult("client-1", "Tears.of.Steel.2012", true, "sent", "Sent."));
        var handler = new DownloadClientRetryGrabJobHandler(grabs.Object, jobs.Object, new Mock<IActivityFeedRepository>().Object, new Mock<IJobScheduler>().Object, TimeProvider.System);
        var payload = Payload().Replace("\"IndexerName\":\"Fixture\"", "\"IndexerName\":null");

        await handler.HandleAsync(TestJobs.Create(SearchExecutionSupport.DownloadClientRetryGrabJobType, payloadJson: payload), CancellationToken.None);

        var recorded = Assert.Single(jobs.Invocations, call => call.Method.Name == nameof(IJobQueueRepository.RecordDownloadDispatchAsync));
        Assert.Equal(string.Empty, recorded.Arguments[5]);
    }
}
