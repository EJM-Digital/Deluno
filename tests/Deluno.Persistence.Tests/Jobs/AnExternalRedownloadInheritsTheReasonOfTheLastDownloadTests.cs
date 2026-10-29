using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The reason a search carries (coverage register, engines): a download started because an outside app asked for another copy is
/// labelled "inherit" (it keeps the reason of the title's last download), whether or not the title still has a file, and is never
/// recorded as a missing or upgrade search. Every other re-grab trigger does the same; a plain scheduled search is told apart by the file.
/// </summary>
public sealed class AnExternalRedownloadInheritsTheReasonOfTheLastDownloadTests
{
    [Theory]
    [InlineData("external-redownload", true)]
    [InlineData("external-redownload", false)]
    [InlineData("recovery-retry", false)]
    [InlineData("grab-rejected", true)]
    [InlineData("health-replacement", true)]
    [InlineData("download-integrity-failed", false)]
    [InlineData("season-pack-partial-import", true)]
    public void A_re_grab_trigger_inherits_whatever_the_file_situation(string trigger, bool hasFile)
    {
        Assert.Equal(DownloadStartedBy.Inherit, DownloadStartedBy.ForSearch(trigger, hasTarget: true, hasFile));
    }

    [Theory]
    [InlineData("schedule", false, "missing")]
    [InlineData("schedule", true, "upgrade")]
    [InlineData("rss", true, "upgrade")]
    [InlineData("manual", false, "missing")]
    public void A_search_nobody_asked_for_by_title_is_missing_or_an_upgrade_by_whether_the_title_has_a_file(string trigger, bool hasFile, string expected)
    {
        Assert.Equal(expected, DownloadStartedBy.ForSearch(trigger, hasTarget: false, hasFile));
    }

    [Fact]
    public void A_person_asking_for_one_title_is_manual()
    {
        Assert.Equal(DownloadStartedBy.Manual, DownloadStartedBy.ForSearch("manual", hasTarget: true, hasFile: true));
    }
}
