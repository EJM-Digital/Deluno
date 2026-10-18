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

    /// <summary>
    /// SABnzbd 5.1.3's own history after the same NZB was sent twice with duplicate detection set to fail the job (the recording
    /// duplicate-failed): the first job completed, and the second is Failed with exactly "Duplicate NZB" in <c>fail_message</c>.
    /// </summary>
    [Fact]
    public void The_failed_job_SABnzbd_really_records_for_a_duplicate_is_read_as_duplicate_and_the_completed_one_as_completed()
    {
        var history = System.Text.Json.Nodes.JsonNode.Parse(Deluno.Tests.Recorded.RecordedReplies.Sabnzbd("duplicate-failed").Find("mode=history").Reply.Body!)!["history"]!["slots"]!.AsArray();
        var failed = Assert.Single(history, slot => slot!["status"]!.GetValue<string>() == "Failed")!;
        var completed = Assert.Single(history, slot => slot!["status"]!.GetValue<string>() == "Completed")!;

        Assert.Equal("Duplicate NZB", failed["fail_message"]!.GetValue<string>());
        Assert.Equal(DownloadClientHistoryOutcomes.Duplicate, SabnzbdDownloadClient.NormalizeHistoryOutcome("Failed", failed["fail_message"]!.GetValue<string>()));
        Assert.Equal(DownloadQueueStatuses.Completed, SabnzbdDownloadClient.NormalizeHistoryOutcome("Completed", completed["fail_message"]!.GetValue<string>()));
    }

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

    /// <summary>
    /// #910: a hard script failure moves the job to Failed, the same status a
    /// genuinely bad release uses — SABnzbd's own free-text fail_message is the
    /// only place that says which, mirroring the duplicate and disk-full checks
    /// above.
    /// </summary>
    [Theory]
    [InlineData("Script was aborted, please check script_log for more information.")]
    [InlineData("Post-processing script CustomProcess.py failed")]
    public void A_failed_status_naming_the_script_is_read_as_a_script_failure_not_a_bad_release(string failMessage)
        => Assert.Equal(DownloadClientHistoryOutcomes.ScriptFailed, SabnzbdDownloadClient.NormalizeHistoryOutcome("Failed", failMessage));

    /// <summary>
    /// #910: a script that only warns does not fail the job at all — SABnzbd
    /// leaves it Completed and notes the warning in fail_message — so today it
    /// was silently read as a plain success. It must surface as a warning
    /// instead, not disappear.
    /// </summary>
    [Fact]
    public void A_completed_status_naming_the_script_is_read_as_a_script_warning_not_a_plain_success()
        => Assert.Equal(
            DownloadClientHistoryOutcomes.ScriptWarning,
            SabnzbdDownloadClient.NormalizeHistoryOutcome("Completed", "Script exit code 1, notify.sh reported a problem"));

    [Fact]
    public void ScriptReason_names_the_script_when_SABnzbd_reports_one()
        => Assert.Equal(
            "Your client's script \"CustomProcess.py\" failed.",
            SabnzbdDownloadClient.ScriptReason("CustomProcess.py", isFailure: true));

    [Fact]
    public void ScriptReason_falls_back_to_a_generic_message_when_no_script_is_named()
        => Assert.Equal(
            "Your client's post-processing script reported a problem.",
            SabnzbdDownloadClient.ScriptReason(null, isFailure: false));
}
