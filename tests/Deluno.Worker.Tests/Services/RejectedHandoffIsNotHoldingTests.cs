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

/// <summary>
/// Seen on the golden VM (3 Nov 2026): a single-file download is handed to Weir under its file name
/// ("Release.mkv"), while the dispatch Deluno made for it carries the release name ("Release"). The title's blockers
/// matched the two by exact name, so a hand-off Weir still held for a single file never read "Weir still has this
/// file". The two are the same download whether they agree on the name or on the client's own item.
/// </summary>
public sealed class ASingleFileHandoffStillHoldsItsTitleTests
{
    [Theory]
    [InlineData("The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO.mkv", "hash-1")]
    [InlineData("The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO.MP4", "hash-1")]
    [InlineData("A.Name.The.Client.Changed.mkv", "hash-1")]
    public async Task A_handoff_named_for_the_single_file_is_still_found_for_its_dispatch(string handoffName, string queueItemId)
    {
        var dispatch = (DownloadDispatchItem)RuntimeHelpers.GetUninitializedObject(typeof(DownloadDispatchItem)) with
        {
            Id = "dispatch-1",
            LibraryId = "proof-library",
            ReleaseName = "The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
            DownloadClientId = "qbittorrent",
            DownloadClientName = "qBittorrent",
            TorrentHashOrItemId = "hash-1",
            CreatedUtc = DateTimeOffset.UnixEpoch
        };
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(repo => repo.QueryDispatchesAsync(It.IsAny<DispatchQueryFilter>(), It.IsAny<DispatchPaginationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(([dispatch], (string?)null));
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorHandoffsAsync("proof-library", 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProcessorHandoffItem(
                    "handoff-1", "proof-library", "movies", "qbittorrent", queueItemId, handoffName,
                    @"C:\Deluno\Proof\Downloads\" + handoffName, "Weir", "submitted",
                    null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            ]);

        var held = await AcquisitionBlockerSources.FindAsync(dispatches.Object, processors.Object, "movies", "movie-1", CancellationToken.None);

        Assert.Equal("Weir", held.ProcessorName);
    }

    [Fact]
    public async Task Another_download_of_another_name_on_the_same_client_does_not_hold_the_title()
    {
        var dispatch = (DownloadDispatchItem)RuntimeHelpers.GetUninitializedObject(typeof(DownloadDispatchItem)) with
        {
            Id = "dispatch-1",
            LibraryId = "proof-library",
            ReleaseName = "The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
            DownloadClientId = "qbittorrent",
            DownloadClientName = "qBittorrent",
            TorrentHashOrItemId = "hash-1",
            CreatedUtc = DateTimeOffset.UnixEpoch
        };
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(repo => repo.QueryDispatchesAsync(It.IsAny<DispatchQueryFilter>(), It.IsAny<DispatchPaginationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(([dispatch], (string?)null));
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorHandoffsAsync("proof-library", 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProcessorHandoffItem(
                    "handoff-2", "proof-library", "movies", "qbittorrent", "hash-2", "Some.Other.Film.2001.1080p.WEB-DL-DELUNO.mkv",
                    @"C:\Deluno\Proof\Downloads\Some.Other.Film.2001.1080p.WEB-DL-DELUNO.mkv", "Weir", "submitted",
                    null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            ]);

        var held = await AcquisitionBlockerSources.FindAsync(dispatches.Object, processors.Object, "movies", "movie-1", CancellationToken.None);

        Assert.Null(held.ProcessorName);
    }
}
