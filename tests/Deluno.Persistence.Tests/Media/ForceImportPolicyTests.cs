using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// #1013: Force Import goes past a short, closed list of minor reasons and
/// nothing else. Media integrity comes first, so these guard the list itself.
/// </summary>
public sealed class ForceImportPolicyTests
{
    /// <summary>
    /// The refusal test: every reason the failure table knows, and every other
    /// reason Deluno raises, is refused unless it is on the closed list.
    /// </summary>
    [Fact]
    public void Only_the_named_safe_reasons_are_ever_forced_and_every_other_reason_is_refused()
    {
        var everyReason = ImportFailurePolicy.KnownReasons
            .Concat(
            [
                ImportFailurePolicy.ProcessorRejected,
                ImportFailurePolicy.DownloadStalled,
                ImportFailurePolicy.ManualFailure,
                ImportFailurePolicy.ProcessedFileIsLess,
                "hardlinkFailed",
                "something-nobody-decided"
            ])
            .Distinct()
            .ToArray();

        foreach (var reason in everyReason)
        {
            var decision = ForceImportPolicy.Decide(reason, enabled: true, timesBlocked: 9, timesForced: 0, maxTries: 25);
            Assert.Equal(ForceImportPolicy.SafeReasons.Contains(reason), decision.Force);
        }

        // The list itself is exactly the one reason where Deluno reached no verdict.
        Assert.Equal([ImportFailurePolicy.MediaProbeUnreadable], ForceImportPolicy.SafeReasons);
    }

    /// <summary>The ones that matter most, by name, so removing one from the guard above cannot go unnoticed.</summary>
    [Theory]
    [InlineData(ImportFailurePolicy.LikelySample)]
    [InlineData(ImportFailurePolicy.NoVideoStream)]
    [InlineData(ImportFailurePolicy.MediaProbeRejected)]
    [InlineData(ImportFailurePolicy.UnsupportedFile)]
    [InlineData(ImportFailurePolicy.Unmatched)]
    [InlineData(ImportFailurePolicy.ReplacementRejected)]
    [InlineData(ImportFailurePolicy.Conflict)]
    [InlineData(ImportFailurePolicy.InsufficientFreeSpace)]
    [InlineData(ImportFailurePolicy.DownloadIntegrityFailed)]
    public void A_verdict_Deluno_reached_is_never_forced(string reason)
        => Assert.False(ForceImportPolicy.IsSafeToForce(reason));

    [Fact]
    public void It_is_off_when_the_switch_is_off()
        => Assert.False(ForceImportPolicy.Decide(ImportFailurePolicy.MediaProbeUnreadable, enabled: false, 5, 0, 3).Force);

    [Fact]
    public void The_first_stop_is_left_to_the_ordinary_retry()
    {
        Assert.False(ForceImportPolicy.Decide(ImportFailurePolicy.MediaProbeUnreadable, true, timesBlocked: 1, 0, 3).Force);
        Assert.True(ForceImportPolicy.Decide(ImportFailurePolicy.MediaProbeUnreadable, true, timesBlocked: 2, 0, 3).Force);
    }

    [Fact]
    public void It_stops_after_the_chosen_number_of_tries()
    {
        Assert.True(ForceImportPolicy.Decide(ImportFailurePolicy.MediaProbeUnreadable, true, 9, timesForced: 2, maxTries: 3).Force);
        var used = ForceImportPolicy.Decide(ImportFailurePolicy.MediaProbeUnreadable, true, 9, timesForced: 3, maxTries: 3);
        Assert.False(used.Force);
        Assert.Contains("all its tries", used.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void It_never_replaces_a_file_that_is_already_there()
        => Assert.False(ForceImportPolicy.Decide(ImportFailurePolicy.MediaProbeUnreadable, true, 9, 0, 3, replacesExistingFile: true).Force);

    [Theory]
    [InlineData(-4, 1)]
    [InlineData(3, 3)]
    [InlineData(5000, ForceImportPolicy.MaxTriesLimit)]
    public void The_number_of_tries_is_kept_within_sensible_bounds(int asked, int expected)
        => Assert.Equal(expected, ForceImportPolicy.ClampMaxTries(asked));
}
