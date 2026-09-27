using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// NZBGet's history status is a MAIN/DETAIL pair (#635, #647, #663). #635
/// taught Deluno to read SUCCESS/UNPACK as completed; this is the failure
/// half: FAILURE, DELETED and WARNING statuses, and the plain-words reason
/// each one gets so a person reads "too many articles are missing" rather
/// than "FAILURE/HEALTH". #663 split DELETED/DUPE, DELETED/COPY and
/// DELETED/GOOD out of that failure bucket: NZBGet's own duplicate check
/// correctly declined a release it already held, which is not a failure and
/// must not blocklist the release or trigger a replacement search.
/// </summary>
public sealed class NzbGetHistoryStatusMappingTests
{
    [Theory]
    [InlineData("SUCCESS/UNPACK", DownloadQueueStatuses.Completed)]
    [InlineData("SUCCESS/NONE", DownloadQueueStatuses.Completed)]
    [InlineData("FAILURE/HEALTH", "failed")]
    [InlineData("FAILURE/PAR", "failed")]
    [InlineData("FAILURE/UNPACK", "failed")]
    [InlineData("FAILURE/MOVE", "failed")]
    [InlineData("WARNING/HEALTH", "failed")]
    [InlineData("WARNING/PAR", "failed")]
    // #733: Sonarr's Nzbget.cs also keeps these two out of its "warning"
    // bucket — a wrong or missing unpack password and a par-check disabled
    // in settings are not recoverable the way a full disk is.
    [InlineData("WARNING/PASSWORD", "failed")]
    [InlineData("WARNING/DAMAGED", "failed")]
    [InlineData("DELETED/HEALTH", "failed")]
    [InlineData("DELETED/SCAN", "failed")]
    [InlineData("DELETED/MANUAL", "failed")]
    public void Outcome_matches_NZBGets_MAIN_DETAIL_status(string status, string expectedOutcome)
        => Assert.Equal(expectedOutcome, NzbGetDownloadClient.NormalizeOutcome(status));

    /// <summary>
    /// #910: FAILURE/SCRIPT and WARNING/SCRIPT are the download itself
    /// finishing but NZBGet's own post-processing script failing or warning —
    /// a setup problem with the script, not a bad release, so neither is read
    /// as "failed" any more (WARNING/SCRIPT used to be silently read as a
    /// plain success, which hid the warning entirely).
    /// </summary>
    [Fact]
    public void Outcome_reads_NZBGets_script_failure_and_warning_apart_from_a_bad_release()
    {
        Assert.Equal(DownloadClientHistoryOutcomes.ScriptFailed, NzbGetDownloadClient.NormalizeOutcome("FAILURE/SCRIPT"));
        Assert.Equal(DownloadClientHistoryOutcomes.ScriptWarning, NzbGetDownloadClient.NormalizeOutcome("WARNING/SCRIPT"));
    }

    [Theory]
    [InlineData("DELETED/DUPE")]
    [InlineData("DELETED/COPY")]
    [InlineData("DELETED/GOOD")]
    public void Outcome_reads_NZBGets_own_duplicate_check_as_duplicate_not_failed(string status)
        => Assert.Equal(DownloadClientHistoryOutcomes.Duplicate, NzbGetDownloadClient.NormalizeOutcome(status));

    /// <summary>
    /// #733: NZBGet's WARNING/SPACE means unpacking stopped because the disk
    /// filled — the release downloaded completely and freeing space plus
    /// re-running post-processing in NZBGet recovers it. Sonarr's Nzbget.cs
    /// (GetHistory) reads UnpackStatus SPACE as a warning rather than a
    /// failure for the same reason; every other WARNING/* detail still maps
    /// to "failed" above.
    /// </summary>
    [Fact]
    public void Outcome_reads_NZBGets_disk_full_warning_as_needing_attention_not_failed()
        => Assert.Equal(DownloadClientHistoryOutcomes.NeedsAttention, NzbGetDownloadClient.NormalizeOutcome("WARNING/SPACE"));

    [Theory]
    [InlineData("FAILURE/HEALTH", "NZBGet could not download it: too many articles are missing.")]
    [InlineData("FAILURE/PAR", "NZBGet could not download it: it could not repair the download; too much data is missing or damaged.")]
    [InlineData("FAILURE/UNPACK", "NZBGet could not download it: unpacking the download failed.")]
    [InlineData("FAILURE/MOVE", "NZBGet could not download it: it could not move the finished files into place.")]
    [InlineData("FAILURE/NONE", "NZBGet could not download it: no reason was given.")]
    [InlineData("WARNING/HEALTH", "NZBGet finished it with a warning: too many articles are missing.")]
    [InlineData("DELETED/HEALTH", "NZBGet deleted it: a health check failed.")]
    [InlineData("DELETED/SCAN", "NZBGet deleted it: the NZB file itself was unreadable.")]
    [InlineData("DELETED/MANUAL", "NZBGet deleted it: something asked NZBGet to remove it.")]
    public void FailureReason_translates_NZBGets_status_into_plain_words(string status, string expected)
        => Assert.Equal(expected, NzbGetDownloadClient.FailureReason(status));

    [Theory]
    [InlineData("DELETED/DUPE", "NZBGet already has this download: it was a duplicate of one NZBGet already held.")]
    [InlineData("DELETED/COPY", "NZBGet already has this download: it was a duplicate of one NZBGet already held.")]
    [InlineData("DELETED/GOOD", "NZBGet already has this download: a better duplicate was kept instead.")]
    public void DuplicateReason_translates_NZBGets_duplicate_status_into_plain_words(string status, string expected)
        => Assert.Equal(expected, NzbGetDownloadClient.DuplicateReason(status));

    [Fact]
    public void AttentionReason_translates_NZBGets_disk_full_warning_into_plain_words()
        => Assert.Equal(
            "NZBGet finished downloading this, but unpacking stopped because the disk is full: free space and re-run post-processing in NZBGet.",
            NzbGetDownloadClient.AttentionReason("WARNING/SPACE"));

    [Fact]
    public void ScriptReason_names_the_failing_script_from_NZBGets_own_ScriptStatuses()
        => Assert.Equal(
            "Your client's script \"CustomProcess.py\" failed.",
            NzbGetDownloadClient.ScriptReason(
            [
                new NzbGetDownloadClient.NzbGetScriptStatus("Unpack", "SUCCESS"),
                new NzbGetDownloadClient.NzbGetScriptStatus("CustomProcess.py", "FAILURE")
            ]));

    [Fact]
    public void ScriptReason_names_a_warning_script_as_warned_not_failed()
        => Assert.Equal(
            "Your client's script \"Notify.py\" warned.",
            NzbGetDownloadClient.ScriptReason([new NzbGetDownloadClient.NzbGetScriptStatus("Notify.py", "WARNING")]));

    [Fact]
    public void ScriptReason_falls_back_when_NZBGet_gives_no_script_statuses()
        => Assert.Equal("Your client's post-processing script reported a problem.", NzbGetDownloadClient.ScriptReason(null));
}
