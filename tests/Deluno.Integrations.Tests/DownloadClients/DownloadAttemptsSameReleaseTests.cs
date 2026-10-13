using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>#1242: a torrent client names a single-file download after the file, the send Deluno recorded is named after the release.</summary>
public sealed class DownloadAttemptsSameReleaseTests
{
    [Theory]
    [InlineData("Film.2020.1080p.WEB-DL.x264-GOLDEN", "Film.2020.1080p.WEB-DL.x264-GOLDEN.mkv")]
    [InlineData("Film.2020.1080p.WEB-DL.x264-GOLDEN.mkv", "film.2020.1080p.web-dl.x264-golden")]
    [InlineData("Film.2020.1080p.WEB-DL.x264-GOLDEN", "Film.2020.1080p.WEB-DL.x264-GOLDEN.nzb")]
    [InlineData("Film.2020.1080p.WEB-DL.x264-GOLDEN", "Film.2020.1080p.WEB-DL.x264-GOLDEN")]
    public void One_release_under_the_clients_name_and_the_sends_name_is_the_same_release(string left, string right)
        => Assert.True(DownloadAttempts.SameRelease(left, right));

    [Theory]
    [InlineData("Film.2020.1080p.WEB-DL.x264-GOLDEN", "Film.2020.2160p.WEB-DL.x264-GOLDEN.mkv")]
    [InlineData("Film.2020.1080p.WEB-DL.x264-GOLDEN", "Film.2020.1080p.WEB-DL.x264-OTHER")]
    [InlineData("Film.2020.1080p.WEB-DL.x264-GOLDEN", "")]
    [InlineData(null, null)]
    public void A_different_release_or_no_name_is_not_the_same_release(string? left, string? right)
        => Assert.False(DownloadAttempts.SameRelease(left, right));
}
