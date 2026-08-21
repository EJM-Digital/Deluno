using Deluno.Media;

namespace Deluno.Movies.Tests;

public sealed class MediaGrabHandlerTests
{
    [Fact]
    public void ValidateReleaseGrab_rejects_missing_release_and_url()
    {
        var errors = MediaGrabHandler.ValidateReleaseGrab(
            new MediaReleaseGrabRequest(
                ReleaseName: " ",
                IndexerId: null,
                IndexerName: null,
                DownloadUrl: null,
                CandidateQuality: null,
                SizeBytes: null,
                Seeders: null,
                Force: null,
                OverrideReason: null));

        Assert.Equal(
            "Choose a release before sending it to a download client.",
            Assert.Single(errors["releaseName"]));
        Assert.Equal(
            "This release does not include a downloadable URL. Choose a different release or check the indexer configuration.",
            Assert.Single(errors["downloadUrl"]));
    }

    [Fact]
    public void ValidateReleaseGrab_accepts_an_absolute_download_url()
    {
        var errors = MediaGrabHandler.ValidateReleaseGrab(
            new MediaReleaseGrabRequest(
                ReleaseName: "Arrival 2016 1080p",
                IndexerId: "indexer-1",
                IndexerName: "Indexer",
                DownloadUrl: "https://indexer.example/download/1",
                CandidateQuality: "1080p",
                SizeBytes: 123,
                Seeders: 4,
                Force: false,
                OverrideReason: null));

        Assert.Empty(errors);
    }
}
