using Deluno.Platform.Contracts;

namespace Deluno.Platform.Tests.Processing;

/// <summary>
/// A hand-off Transfers counts as needing a person can always be ended (#570).
///
/// <para><b>Why this file exists.</b> Dismissal used to be a hand-picked list of
/// endings, and the list disagreed with what the screen counts. On the rig a
/// film was removed while Weir still had its file; Weir finished anyway and
/// reported it, so the hand-off ended <c>completed</c> carrying "the film was
/// removed, so Deluno did not import it" — counted under Needs attention, and
/// offered neither retry, nor import-original, nor dismiss, because each wanted
/// a different status. It could not leave the screen.</para>
///
/// <para>The screen's rule is `handoffNeedsPerson` in
/// <c>apps/web/src/lib/needs-action.ts</c>: not dismissed, and either an ending
/// that failed or carrying a reason. This mirrors it, so the two cannot drift
/// apart without one of them going red.</para>
/// </summary>
public sealed class WhatIsCountedCanBeClearedTests
{
    private const string Reason = "Weir produced a cleaned file, but the film was removed from Deluno.";

    [Fact]
    public void Every_hand_off_a_person_is_asked_about_can_be_dismissed()
    {
        // Read from the statuses themselves, so a new one is covered the day it
        // is added rather than the day someone remembers this file. Each one
        // carries a reason, which is what makes the screen count it.
        var stuck = ProcessorHandoffStatuses.All
            .Where(status => ProcessorHandoffStatuses.NeedsPerson(status, Reason))
            .Where(status => !ProcessorHandoffStatuses.CanDismiss(status, Reason))
            .ToArray();

        Assert.Empty(stuck);
    }

    /// <summary>
    /// A hand-off still with the processor is not counted and not dismissed out
    /// from under it: Stop waiting ends it (#566), and it can be cleared once
    /// it has.
    /// </summary>
    [Fact]
    public void A_hand_off_still_with_the_processor_is_neither_counted_nor_dismissable()
    {
        foreach (var status in ProcessorHandoffStatuses.All.Where(ProcessorHandoffStatuses.IsWithProcessor))
        {
            Assert.False(ProcessorHandoffStatuses.NeedsPerson(status, Reason), status);
            Assert.False(ProcessorHandoffStatuses.CanDismiss(status, Reason), status);
        }
    }

    /// <summary>Nothing was said about it and it worked, so there is nothing to clear.</summary>
    [Fact]
    public void A_hand_off_that_finished_well_and_says_nothing_is_not_asked_about()
    {
        Assert.False(ProcessorHandoffStatuses.NeedsPerson(ProcessorHandoffStatuses.Completed, null));
        Assert.False(ProcessorHandoffStatuses.CanDismiss(ProcessorHandoffStatuses.Completed, null));
        Assert.False(ProcessorHandoffStatuses.CanDismiss(ProcessorHandoffStatuses.Released, null));
    }

    [Fact]
    public void Dismissing_twice_is_not_offered()
        => Assert.False(ProcessorHandoffStatuses.CanDismiss(ProcessorHandoffStatuses.Dismissed, Reason));

    /// <summary>The case from the rig, named so a regression says what broke.</summary>
    [Fact]
    public void A_processed_file_that_arrived_after_its_title_went_can_be_dismissed()
        => Assert.True(ProcessorHandoffStatuses.CanDismiss(ProcessorHandoffStatuses.Completed, Reason));
}
