using Deluno.Integrations.Search;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// An indexer's retention window and a title's availability delay both need a date. A release that is older than the window is
/// refused, one with no age cannot be shown to be inside it, and a title with no availability date cannot show its wait has passed.
/// Refs #1268.
/// </summary>
public sealed class AUsenetReleaseOutsideItsRetentionOrWithNoDateIsSaidTests
{
    private static ReleaseDecision Decide(int? retentionDays = null, double? ageHours = null, int? availabilityDelayDays = null, DateTimeOffset? availableUtc = null)
        => ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Some.Film.2020.1080p.WEB-DL.x264-GRP",
            Quality: "WEB 1080p",
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            SizeBytes: 8L * 1024 * 1024 * 1024,
            Seeders: null,
            DownloadUrl: "https://example.com/release.nzb",
            SourcePriorityScore: 0,
            CustomFormatScore: 0,
            ReleaseAgeHours: ageHours,
            RetentionDays: retentionDays,
            AvailableUtc: availableUtc,
            AvailabilityDelayDays: availabilityDelayDays));

    [Fact]
    public void A_release_older_than_the_indexers_retention_window_is_refused_in_words()
    {
        var decision = Decide(retentionDays: 30, ageHours: 31 * 24);

        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Contains(decision.Reasons, reason => reason.Contains("older than this indexer's 30-day retention window", StringComparison.Ordinal));
    }

    [Fact]
    public void A_release_inside_the_retention_window_is_not_refused_for_it()
    {
        var decision = Decide(retentionDays: 30, ageHours: 5 * 24);

        Assert.DoesNotContain(decision.Reasons, reason => reason.Contains("retention", StringComparison.Ordinal));
    }

    [Fact]
    public void A_release_with_no_age_cannot_be_shown_to_be_inside_the_retention_window_so_it_is_refused()
    {
        var decision = Decide(retentionDays: 30, ageHours: null);

        Assert.Equal(ReleaseDecisionStatuses.Rejected, decision.Status);
        Assert.Contains(decision.Reasons, reason => reason.Contains("30-day retention limit cannot be verified", StringComparison.Ordinal));
    }

    [Fact]
    public void A_title_with_no_availability_date_cannot_show_its_delay_has_passed_so_the_release_is_delayed()
    {
        var decision = Decide(availabilityDelayDays: 7, availableUtc: null);

        Assert.Equal("delayed", decision.Status);
        Assert.Contains(decision.RiskFlags, flag => flag.Contains("Availability date is unavailable", StringComparison.Ordinal));
    }
}
