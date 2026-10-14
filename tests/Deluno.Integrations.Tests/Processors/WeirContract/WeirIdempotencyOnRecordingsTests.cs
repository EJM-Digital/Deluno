using System.Text.Json.Nodes;
using Deluno.Platform.Contracts;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.Processors.WeirContract.WeirContract;

namespace Deluno.Integrations.Tests.Processors.WeirContract;

/// <summary>
/// #1301: the Idempotency-Key on a hand-off to Weir. The same unchanged hand-off carries the same key, so a retry is
/// not worked twice; a hand-off put back to waiting (its row changed) carries a new one, so Weir processes it again.
/// Weir's reply is its recorded acceptance of a hand-off.
/// </summary>
public sealed class WeirIdempotencyOnRecordingsTests
{
    private static ProcessorHandoffItem HandoffFor(Recording recording, DateTimeOffset updated)
    {
        var body = JsonNode.Parse(recording.Exchanges[0].Request.Body!)!.AsObject();
        return WeirContractClientTests.Handoff(
            body["handoffId"]!.GetValue<string>(), body["libraryId"]?.GetValue<string>() ?? "library-1",
            body["mediaType"]?.GetValue<string>() ?? "movie", body["sourcePath"]!.GetValue<string>(), body["releaseName"]?.GetValue<string>() ?? "Release")
            with { UpdatedUtc = updated };
    }

    [Fact]
    public async Task The_same_unchanged_hand_off_is_sent_with_the_same_key_and_one_put_back_to_waiting_with_a_new_one()
    {
        var recording = Rec("handoff-accepted");
        var replay = new WeirReplay(recording);
        var service = Service(replay);
        var first = HandoffFor(recording, DateTimeOffset.UnixEpoch.AddMinutes(5));

        var one = await service.SubmitAsync(Connection, first, CancellationToken.None);
        var retry = await service.SubmitAsync(Connection, first, CancellationToken.None);
        var putBack = await service.SubmitAsync(Connection, first with { UpdatedUtc = first.UpdatedUtc.AddMinutes(10) }, CancellationToken.None);

        Assert.True(one.IsAccepted && retry.IsAccepted && putBack.IsAccepted);
        var keys = replay.Sent.Select(sent => sent.Headers["Idempotency-Key"]).ToArray();
        Assert.Equal(3, keys.Length);
        Assert.Equal(keys[0], keys[1]);
        Assert.NotEqual(keys[0], keys[2]);
        Assert.StartsWith(first.Id + ":", keys[0]);
        Assert.StartsWith(first.Id + ":", keys[2]);
    }
}
