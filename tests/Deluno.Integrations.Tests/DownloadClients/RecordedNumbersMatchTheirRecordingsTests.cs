using Deluno.Tests.Recorded;
using Xunit;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1535: a few tests write a size or an id out because the number is the point of the test (a job stuck a few
/// hundred kilobytes short of its end, a paused size). Each one must be a number its recording holds. When a recording is
/// made again and the number moves, this fails and names the recording, instead of the test going quietly wrong.
/// </summary>
public sealed class RecordedNumbersMatchTheirRecordingsTests
{
    [Theory]
    // AnNzbgetJobStuckNearTheEndIsNotCalledQueuedTests: the size and the bytes already done of the stuck job.
    [InlineData("nzbget", "stalled-near-end", "595073366")]
    [InlineData("nzbget", "stalled-near-end", "594731554")]
    // EveryClientPausesAndResumesOnlyWhatItHasTests: the paused size.
    [InlineData("nzbget", "paused", "1688311206")]
    // UsenetClientOperationsOnRecordingsTests: the id inside the grab link.
    [InlineData("nzbget", "category-moved", "6925692986")]
    public void A_number_written_out_in_a_test_is_still_in_its_recording(string app, string state, string number)
    {
        var holds = RecordedReplies.LoadLatest(app, state).Matches($@"(?<![0-9]){number}(?![0-9])");

        Assert.True(holds.Count > 0, $"{app} {state} no longer holds {number}. The recording changed; update the test that writes it out.");
    }
}
