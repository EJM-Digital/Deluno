using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// SABnzbd's history status has no MAIN/DETAIL split the way NZBGet's does
/// (see <see cref="NzbGetHistoryStatusMappingTests"/>): "Failed" alone covers
/// a genuinely bad release, a duplicate SABnzbd's own detection declined
/// (#886), and a full disk during unpacking (#884) — the only place SABnzbd
/// says which is the free-text <c>fail_message</c>.
/// </summary>
public sealed class SabnzbdHistoryStatusMappingTests
{
    [Theory]
    [InlineData("Completed", "")]
    [InlineData("Succeeded", "")]
    public void A_completed_status_is_read_as_completed_regardless_of_fail_message(string status, string failMessage)
        => Assert.Equal(DownloadQueueStatuses.Completed, SabnzbdDownloadClient.NormalizeHistoryOutcome(status, failMessage));

    [Theory]
    [InlineData("Aborted, ran out of articles")]
    [InlineData("")]
    public void A_failed_status_with_no_duplicate_or_disk_full_wording_is_read_as_failed(string failMessage)
        => Assert.Equal("failed", SabnzbdDownloadClient.NormalizeHistoryOutcome("Failed", failMessage));

    [Fact]
    public void Duplicate_NZB_is_read_as_duplicate_not_failed()
        => Assert.Equal(DownloadClientHistoryOutcomes.Duplicate, SabnzbdDownloadClient.NormalizeHistoryOutcome("Failed", "Duplicate NZB"));

    [Theory]
    [InlineData("Unpacking failed, disk full")]
    [InlineData("Unpacking failed, write error or disk is full?")]
    [InlineData("Repairing failed, Disk full")]
    public void Disk_full_wording_is_read_as_needing_attention_not_failed(string failMessage)
        => Assert.Equal(DownloadClientHistoryOutcomes.NeedsAttention, SabnzbdDownloadClient.NormalizeHistoryOutcome("Failed", failMessage));

    [Fact]
    public void FailureReason_translates_SABnzbds_message_into_plain_words()
        => Assert.Equal(
            "SABnzbd could not download it: Aborted, ran out of articles.",
            SabnzbdDownloadClient.FailureReason("Aborted, ran out of articles"));

    [Fact]
    public void DuplicateReason_translates_SABnzbds_duplicate_message_into_plain_words()
        => Assert.Equal(
            "SABnzbd already has this download: Duplicate NZB.",
            SabnzbdDownloadClient.DuplicateReason("Duplicate NZB"));

    [Fact]
    public void AttentionReason_translates_SABnzbds_disk_full_message_into_plain_words()
        => Assert.Equal(
            "SABnzbd finished downloading this, but ran out of disk space during unpacking: Unpacking failed, write error or disk is full? Free space, then retry it in SABnzbd.",
            SabnzbdDownloadClient.AttentionReason("Unpacking failed, write error or disk is full?"));
}
