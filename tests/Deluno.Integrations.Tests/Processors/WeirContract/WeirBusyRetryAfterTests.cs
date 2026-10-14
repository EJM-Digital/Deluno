using Deluno.Platform.Contracts;
using static Deluno.Integrations.Tests.Processors.WeirContract.WeirContract;

namespace Deluno.Integrations.Tests.Processors.WeirContract;

/// <summary>
/// Weir 1.0.0-rc.18 answers a write that waited the whole busy timeout with <c>503</c>, a <c>Retry-After</c> header and the code
/// <c>database_busy</c> (Weir #964). The reply is a recording (<c>weir/1.0.0-rc.18/database-busy-outcome</c>, made by
/// <c>scripts/recorded-replies/Record-WeirErrors.ps1</c>), never one written here.
///
/// <para>Deluno treats any 5xx as "not now" (<see cref="WeirPlainTextFiveHundredTests"/>) and asks again on its own schedule, but it does
/// not read <c>Retry-After</c>, so it can ask again sooner than Weir said it would be free. This test states what Deluno must do with
/// it: the answer stays owed, the hand-off is not given up on, and the result carries the time Weir named so the next ask can wait at
/// least that long. The same applies to a hand-off (<c>POST /intake/webhook/deluno</c>), which the recording may also hold.</para>
///
/// <para>SKIPPED until the rc.18 recording exists: the state name is held in a constant, not passed to <c>ReplayOf("...")</c> in the
/// source, so the guards that scan the tests for recorded names do not fail on a recording that is not there yet. When the recording is
/// in, remove the skip and make the failing assertion pass: <see cref="ProcessorOutcomeResult"/> (and the submission, status and cancel
/// results) gain a <c>RetryAfter</c> read from the header, and the planner's next attempt is no earlier than that.</para>
/// </summary>
public sealed class WeirBusyRetryAfterTests
{
    private const string BusyState = "database-busy-outcome";
    private const string HandoffId = "recorded-no-such-handoff";
    private const string SkipReason = "Waits for the Weir 1.0.0-rc.18 recording weir/1.0.0-rc.18/database-busy-outcome.json (Record-WeirErrors.ps1, tests/fixtures/recorded/MISSING.md). Deluno does not yet honour Retry-After.";

    [Fact(Skip = SkipReason)]
    public async Task An_outcome_answered_503_database_busy_stays_owed_and_names_how_long_Weir_asked_to_wait()
    {
        var recorded = Rec(BusyState).Single;
        Assert.Equal(503, recorded.Reply.Status);
        var seconds = int.Parse(recorded.Reply.Headers["retry-after"], System.Globalization.CultureInfo.InvariantCulture);

        var result = await Service(ReplayOf(BusyState)).ReportOutcomeAsync(
            Connection, HandoffId, ProcessorHandoffOutcome.Imported, "/library/x.mkv", null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unreachable, result.Answer);
        Assert.False(result.Released);
        var retryAfter = result.GetType().GetProperty("RetryAfter")?.GetValue(result) as TimeSpan?;
        Assert.True(retryAfter == TimeSpan.FromSeconds(seconds), $"The result should carry Weir's Retry-After of {seconds} s; it carried {retryAfter}.");
    }
}
