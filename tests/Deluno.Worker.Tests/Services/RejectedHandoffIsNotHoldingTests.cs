using System.Runtime.CompilerServices;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #497, seen on the rig: Weir rejected and deleted The Terror's file, and the
/// title still read "Weir still has this file". Only a hand-off that is still
/// with the processor holds a title; rejected, failed, cancelled and the other
/// endings are the record of a finished cycle.
/// </summary>
public sealed class RejectedHandoffIsNotHoldingTests
{
    [Theory]
    [InlineData("waiting", true)]
    [InlineData("submitted", true)]
    [InlineData("accepted", true)]
    [InlineData("started", true)]
    [InlineData("rejected", false)]
    [InlineData("failed", false)]
    [InlineData("completed", false)]
    [InlineData("cancelled", false)]
    [InlineData("released", false)]
    [InlineData("timed-out", false)]
    public async Task Only_a_handoff_still_with_the_processor_holds_the_title(string status, bool holds)
    {
        var dispatch = (DownloadDispatchItem)RuntimeHelpers.GetUninitializedObject(typeof(DownloadDispatchItem)) with
        {
            Id = "dispatch-1",
            LibraryId = "proof-library",
            ReleaseName = "The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
            DownloadClientId = "qbittorrent",
            DownloadClientName = "qBittorrent",
            CreatedUtc = DateTimeOffset.UnixEpoch
        };
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(repo => repo.QueryDispatchesAsync(It.IsAny<DispatchQueryFilter>(), It.IsAny<DispatchPaginationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(([dispatch], (string?)null));
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorHandoffsAsync("proof-library", 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProcessorHandoffItem(
                    "handoff-1", "proof-library", "movies", "qbittorrent", "hash-1", dispatch.ReleaseName,
                    @"C:\Deluno\Proof\Downloads\The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO", "Weir", status,
                    null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            ]);

        var held = await AcquisitionBlockerSources.FindAsync(dispatches.Object, processors.Object, "movies", "movie-1", CancellationToken.None);

        Assert.Equal(holds ? "Weir" : null, held.ProcessorName);
    }
}
