using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// NZBGet's history status is a MAIN/DETAIL pair (#635, #647). #635 taught
/// Deluno to read SUCCESS/UNPACK as completed; this is the failure half:
/// FAILURE, DELETED and WARNING statuses, and the plain-words reason each one
/// gets so a person reads "too many articles are missing" rather than
/// "FAILURE/HEALTH".
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
    [InlineData("DELETED/DUPE", "failed")]
    [InlineData("DELETED/COPY", "failed")]
    [InlineData("DELETED/GOOD", "failed")]
    [InlineData("DELETED/SCAN", "failed")]
    [InlineData("DELETED/MANUAL", "failed")]
    public void Outcome_matches_NZBGets_MAIN_DETAIL_status(string status, string expectedOutcome)
        => Assert.Equal(expectedOutcome, NzbGetDownloadClient.NormalizeOutcome(status));

    [Theory]
    [InlineData("FAILURE/HEALTH", "NZBGet could not download it: too many articles are missing.")]
    [InlineData("FAILURE/PAR", "NZBGet could not download it: it could not repair the download; too much data is missing or damaged.")]
    [InlineData("FAILURE/UNPACK", "NZBGet could not download it: unpacking the download failed.")]
    [InlineData("FAILURE/MOVE", "NZBGet could not download it: it could not move the finished files into place.")]
    [InlineData("FAILURE/SCRIPT", "NZBGet could not download it: a post-processing script failed it.")]
    [InlineData("FAILURE/NONE", "NZBGet could not download it: no reason was given.")]
    [InlineData("WARNING/HEALTH", "NZBGet finished it with a warning: too many articles are missing.")]
    [InlineData("DELETED/HEALTH", "NZBGet deleted it: a health check failed.")]
    [InlineData("DELETED/DUPE", "NZBGet deleted it: it was a duplicate of one NZBGet already held.")]
    [InlineData("DELETED/COPY", "NZBGet deleted it: it was a duplicate of one NZBGet already held.")]
    [InlineData("DELETED/GOOD", "NZBGet deleted it: a better duplicate was kept instead.")]
    [InlineData("DELETED/SCAN", "NZBGet deleted it: the NZB file itself was unreadable.")]
    [InlineData("DELETED/MANUAL", "NZBGet deleted it: something asked NZBGet to remove it.")]
    public void FailureReason_translates_NZBGets_status_into_plain_words(string status, string expected)
        => Assert.Equal(expected, NzbGetDownloadClient.FailureReason(status));
}
