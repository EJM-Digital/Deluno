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
    [InlineData("WARNING/SCRIPT", DownloadQueueStatuses.Completed)]
    [InlineData("FAILURE/HEALTH", "failed")]
    [InlineData("FAILURE/PAR", "failed")]
    [InlineData("FAILURE/UNPACK", "failed")]
    [InlineData("FAILURE/MOVE", "failed")]
    [InlineData("WARNING/HEALTH", "failed")]
    [InlineData("WARNING/PAR", "failed")]
    [InlineData("DELETED/HEALTH", "failed")]
    [InlineData("DELETED/SCAN", "failed")]
    [InlineData("DELETED/MANUAL", "failed")]
    public void Outcome_matches_NZBGets_MAIN_DETAIL_status(string status, string expectedOutcome)
        => Assert.Equal(expectedOutcome, NzbGetDownloadClient.NormalizeOutcome(status));

    [Theory]
    [InlineData("DELETED/DUPE")]
    [InlineData("DELETED/COPY")]
    [InlineData("DELETED/GOOD")]
    public void Outcome_reads_NZBGets_own_duplicate_check_as_duplicate_not_failed(string status)
        => Assert.Equal(DownloadClientHistoryOutcomes.Duplicate, NzbGetDownloadClient.NormalizeOutcome(status));

    [Theory]
    [InlineData("FAILURE/HEALTH", "NZBGet could not download it: too many articles are missing.")]
    [InlineData("FAILURE/PAR", "NZBGet could not download it: it could not repair the download; too much data is missing or damaged.")]
    [InlineData("FAILURE/UNPACK", "NZBGet could not download it: unpacking the download failed.")]
    [InlineData("FAILURE/MOVE", "NZBGet could not download it: it could not move the finished files into place.")]
    [InlineData("FAILURE/SCRIPT", "NZBGet could not download it: a post-processing script failed it.")]
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
}
