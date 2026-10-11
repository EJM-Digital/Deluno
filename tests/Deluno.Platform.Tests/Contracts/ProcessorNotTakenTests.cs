using Deluno.Jobs.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>#1250: when a hand-off the processor did not take is sent again, and how many tries it has had.</summary>
public sealed class ProcessorNotTakenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static ProcessorHandoffItem Waiting(DateTimeOffset updated, string? message = null)
        => new("h-1", "lib", "movies", "client", "item", "Film.2021", "/downloads/Film.2021", "Weir",
            ProcessorHandoffStatuses.Waiting, null, null, message ?? ProcessorNotTaken.BusyMessage("Weir"), updated.AddHours(-1), updated);

    private static ActivityEventItem Line(string category, int minutesAgo)
        => new("a", category, "x", null, null, "processor-handoff", "h-1", Now.AddMinutes(-minutesAgo));

    [Fact]
    public void The_hand_off_is_sent_again_after_two_then_five_then_ten_minutes()
    {
        Assert.Equal([2d, 5d, 10d], ProcessorNotTaken.RetryAfter.Select(span => span.TotalMinutes));

        foreach (var (tries, minutes) in new[] { (1, 2), (2, 5), (3, 10) })
        {
            var updated = Now.AddMinutes(-minutes);
            Assert.False(ProcessorNotTaken.RetryNotDue(Waiting(updated.AddSeconds(-1)), tries, Now));
            Assert.True(ProcessorNotTaken.RetryNotDue(Waiting(updated.AddSeconds(1)), tries, Now));
        }
    }

    [Fact]
    public void A_hand_off_that_was_not_refused_or_is_not_waiting_is_never_held_back()
    {
        Assert.False(ProcessorNotTaken.RetryNotDue(Waiting(Now), 0, Now));
        Assert.False(ProcessorNotTaken.RetryNotDue(Waiting(Now, "Weir never picked this up."), 1, Now));
        Assert.False(ProcessorNotTaken.RetryNotDue(Waiting(Now) with { Status = ProcessorHandoffStatuses.Submitted }, 1, Now));
    }

    [Fact]
    public void Tries_are_counted_since_the_last_time_it_became_a_problem()
    {
        Assert.Equal(0, ProcessorNotTaken.TriesSoFar([]));
        Assert.Equal(2, ProcessorNotTaken.TriesSoFar([Line("processing.not-taken", 9), Line("processing.submitted", 8), Line("processing.not-taken", 5)]));
        Assert.Equal(1, ProcessorNotTaken.TriesSoFar([
            Line("processing.not-taken", 60), Line("processing.not-taken", 50), Line("processing.not-taken", 40),
            Line("processing.not-taken.gave-up", 30), Line("processing.not-taken", 2)]));
    }

    [Fact]
    public void The_words_are_plain_and_name_the_cause()
    {
        Assert.Equal("Weir is busy with this file for another connection; Deluno will send it again shortly.", ProcessorNotTaken.BusyMessage("Weir"));
        var gaveUp = ProcessorNotTaken.GaveUpMessage("Weir", "Film.2021");
        Assert.Contains("another connection", gaveUp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("share one download folder", gaveUp);
        Assert.DoesNotContain("not_taken", gaveUp);
        Assert.True(ProcessorFailureClasses.IsNotTaken(" Not_Taken "));
        Assert.False(ProcessorFailureClasses.IsNotTaken("source_gone"));
    }
}
