using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Deluno.Platform.Contracts;
using Deluno.Tests.Recorded;
using Moq;
using static Deluno.Integrations.Tests.Processors.WeirContract.WeirContract;

namespace Deluno.Integrations.Tests.Processors.WeirContract;

/// <summary>
/// #1239: what Deluno really sends Weir. The requests in tests/fixtures/recorded/deluno/&lt;version&gt;/ were written down by a
/// recording proxy between the real Deluno (rc.30.30) and the real Weir (1.0.0-rc.13) on the golden VM, with their headers
/// (the secret header and Idempotency-Key). Nothing here is built from the documented shape: Deluno's client sends the same
/// call for the same hand-off, and the request it sends is held against the recorded one, member for member and header for
/// header; Weir's recorded reply is what the client then reads.
/// Weir's own error replies (a wrong secret, an unknown id, a refused body) are the recordings under weir/.
/// </summary>
public sealed class WeirRealExchangeTests
{
    // ----- the hand-off Deluno sends -----

    [Theory]
    [InlineData("handoff-single-file", 0)]
    [InlineData("handoff-release-folder", 0)]
    [InlineData("handoff-usenet-folder", 0)]
    [InlineData("handoff-resend", 0)]
    [InlineData("handoff-resend", 1)]
    // Deluno 1.0.0-rc.30.33 into Weir 1.0.0-rc.18, recorded in Docker through a proxy between the two (scripts/recorded-replies/docker/flows.mjs):
    // a name already in Weir's output (both collision policies), a season pack, a pack with a file that cannot be read, a release Weir rejects,
    // and a send through a second processor connection to the same Weir.
    [InlineData("handoff-collision-replace", 0)]
    [InlineData("handoff-collision-keep-both", 0)]
    [InlineData("handoff-season-pack", 0)]
    [InlineData("handoff-partial-season-pack", 0)]
    [InlineData("handoff-rejected-release", 0)]
    [InlineData("handoff-second-connection", 0)]
    public async Task The_request_Deluno_sends_for_a_hand_off_is_the_request_it_was_recorded_sending(string state, int exchange)
    {
        var recording = RecordedReplies.Deluno(state);
        var recorded = recording.Exchanges[exchange];
        var replay = new WeirReplay(recording);

        var result = await Submit(recorded, replay);

        Assert.True(result.IsAccepted, result.Message);
        var sent = Assert.Single(replay.Sent);
        Assert.Equal("POST", sent.Method);
        Assert.Equal(recorded.Request.Path, sent.Path);
        Assert.Equal(Body(recorded.Request.Body).AsObject().Select(p => p.Key), Body(sent.Body).AsObject().Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(Body(recorded.Request.Body), Body(sent.Body)), $"Deluno sent {sent.Body}, it was recorded sending {recorded.Request.Body}.");
        AssertHeaders(recorded.Request, sent, expectIdempotencyKey: true);
        Assert.StartsWith("application/json", sent.Headers.TryGetValue("Content-Type", out var type) ? type : "application/json");
    }

    [Fact]
    public void A_resend_carries_the_same_source_under_a_new_hand_off_id_and_a_new_key()
    {
        var resend = RecordedReplies.Deluno("handoff-resend").Exchanges;
        var first = Body(resend[0].Request.Body);
        var second = Body(resend[1].Request.Body);

        Assert.Equal(first["sourcePath"]!.GetValue<string>(), second["sourcePath"]!.GetValue<string>());
        Assert.NotEqual(first["handoffId"]!.GetValue<string>(), second["handoffId"]!.GetValue<string>());
        Assert.NotEqual(resend[0].Request.Headers!["idempotency-key"], resend[1].Request.Headers!["idempotency-key"]);
        Assert.All(resend, e => Assert.Equal(200, e.Reply.Status));
    }

    [Fact]
    public void A_single_file_download_is_handed_over_as_the_file_and_a_release_folder_as_the_folder()
    {
        var single = Body(RecordedReplies.Deluno("handoff-single-file").Exchanges[0].Request.Body);
        var folder = Body(RecordedReplies.Deluno("handoff-release-folder").Exchanges[0].Request.Body);
        var usenet = Body(RecordedReplies.Deluno("handoff-usenet-folder").Exchanges[0].Request.Body);

        Assert.EndsWith(".mkv", single["sourcePath"]!.GetValue<string>());
        Assert.DoesNotContain(".mkv", folder["sourcePath"]!.GetValue<string>());
        // A Usenet job is a folder, and Deluno names the file inside it: Weir keeps the folder in its output.
        Assert.Matches(@"\\([^\\]+)\\\1\.mkv$", usenet["sourcePath"]!.GetValue<string>());
        Assert.Null(single["sourceFiles"]);
    }

    /// <summary>
    /// Where Weir really puts the output (#1262). A single-file source lands flat in the output folder: the output path is the
    /// file, with no folder named after the source. A release folder keeps its folder. Both read from Weir's own status replies
    /// for hand-offs the real Deluno made.
    /// </summary>
    [Fact]
    public void Weirs_output_for_a_single_file_source_is_flat_and_for_a_release_folder_is_inside_a_folder_named_after_it()
    {
        var single = Body(Rec("real-single-file-status-completed").Exchanges[0].Reply.Body)["outputPath"]!.GetValue<string>();
        var folder = Body(Rec("real-release-folder-status-completed").Exchanges[0].Reply.Body)["outputPath"]!.GetValue<string>();
        var usenet = Body(Rec("real-usenet-folder-status-completed").Exchanges[0].Reply.Body)["outputPath"]!.GetValue<string>();

        Assert.Matches(@"^C:\\Weir\\Ready\\Movies\\[^\\]+\.mkv$", single);
        Assert.Matches(@"^C:\\Weir\\Ready\\Movies\\([^\\]+)\\\1\.mkv$", folder);
        Assert.Matches(@"^C:\\Weir\\Ready\\Movies\\([^\\]+)\\\1\.mkv$", usenet);
    }

    // ----- the outcome Deluno tells -----

    [Theory]
    [InlineData("outcome-imported", 0)]
    [InlineData("outcome-imported", 1)]
    [InlineData("outcome-imported-for-replaced-send", 0)]
    [InlineData("outcome-not-imported-removed", 0)]
    [InlineData("outcome-not-imported-removed", 1)]
    public async Task The_outcome_Deluno_tells_is_the_outcome_it_was_recorded_telling(string state, int exchange)
    {
        var recording = RecordedReplies.Deluno(state);
        var recorded = recording.Exchanges[exchange];
        var replay = new WeirReplay(recording);

        var result = await ReportOutcome(recorded, replay);

        Assert.Equal(ProcessorAnswer.Answered, result.Answer);
        Assert.Equal(Body(recorded.Reply.Body)["released"]!.GetValue<bool>(), result.Released);
        Assert.Equal(Body(recorded.Reply.Body)["message"]!.GetValue<string>(), result.Message);
        var sent = Assert.Single(replay.Sent);
        Assert.Equal("POST", sent.Method);
        Assert.Equal(recorded.Request.Path, sent.Path);
        AssertHeaders(recorded.Request, sent, expectIdempotencyKey: false);
        var wanted = Body(recorded.Request.Body).AsObject();
        var actual = Body(sent.Body).AsObject();
        Assert.Equal(wanted.Select(p => p.Key), actual.Select(p => p.Key));
        Assert.Equal(
            DateTimeOffset.Parse(wanted["occurredUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(actual["occurredUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        wanted.Remove("occurredUtc");
        actual.Remove("occurredUtc");
        Assert.True(JsonNode.DeepEquals(wanted, actual), $"Deluno sent {sent.Body}, it was recorded sending {recorded.Request.Body}.");
    }

    // ----- the status poll, the cancel and the two asks -----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task The_status_poll_Deluno_sends_is_the_one_it_was_recorded_sending_and_a_queued_hand_off_is_read_as_queued(int exchange)
    {
        var recording = RecordedReplies.Deluno("status-ask-queued");
        var recorded = recording.Exchanges[exchange];
        var replay = new WeirReplay(recording);

        var status = await Service(replay).GetHandoffStatusAsync(Connection, IdOfPath(recorded.Request.Path), CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Answered, status.Answer);
        Assert.Equal(ProcessorHandoffStates.Queued, status.State);
        Assert.Equal(1, status.QueuePosition);
        var sent = Assert.Single(replay.Sent);
        Assert.Equal("GET", sent.Method);
        Assert.Equal(recorded.Request.Path, sent.Path);
        Assert.Null(sent.Body);
        AssertHeaders(recorded.Request, sent, expectIdempotencyKey: false);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_cancel_of_a_queued_hand_off_is_the_DELETE_it_was_recorded_sending_and_the_204_is_answered(int exchange)
    {
        var recording = RecordedReplies.Deluno("cancel-queued");
        var recorded = recording.Exchanges[exchange];
        var replay = new WeirReplay(recording);

        var result = await Service(replay).CancelHandoffAsync(Connection, IdOfPath(recorded.Request.Path), CancellationToken.None);

        Assert.Equal(204, recorded.Reply.Status);
        Assert.Equal(ProcessorAnswer.Answered, result.Answer);
        var sent = Assert.Single(replay.Sent);
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal(recorded.Request.Path, sent.Path);
        Assert.Null(sent.Body);
        AssertHeaders(recorded.Request, sent, expectIdempotencyKey: false);
    }

    [Fact]
    public async Task The_capabilities_ask_Deluno_sends_is_the_one_it_was_recorded_sending()
    {
        var recording = RecordedReplies.Deluno("capabilities-ask");
        var recorded = recording.Exchanges[0];
        var replay = new WeirReplay(recording);

        var support = await Service(replay).GetStatusSupportAsync(Connection, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Answered, support.Answer);
        Assert.True(support.CanReportStatus);
        Assert.True(support.CanCancel);
        var sent = Assert.Single(replay.Sent);
        Assert.Equal(recorded.Request.Path, sent.Path);
        AssertHeaders(recorded.Request, sent, expectIdempotencyKey: false);
    }

    [Fact]
    public async Task The_library_folders_ask_Deluno_sends_is_the_one_it_was_recorded_sending()
    {
        var recording = RecordedReplies.Deluno("library-folders-ask");
        var recorded = recording.Exchanges[0];
        var replay = new WeirReplay(recording);

        var folders = await Service(replay).GetLibraryFoldersAsync(Connection, CancellationToken.None);

        Assert.NotEmpty(folders);
        var sent = Assert.Single(replay.Sent);
        Assert.Equal(recorded.Request.Path, sent.Path);
        AssertHeaders(recorded.Request, sent, expectIdempotencyKey: false);
    }

    // ----- Weir's real error replies -----

    [Theory]
    [InlineData("status-wrong-secret")]
    [InlineData("status-no-secret")]
    // The secret of a different connection of the same kind than the one that sent the hand-off: the same 401 and the same words.
    [InlineData("status-other-connections-secret")]
    public async Task A_status_poll_Weir_refuses_for_its_secret_is_forbidden_and_says_to_check_the_secret(string state)
    {
        var recording = Rec(state);
        var status = await Service(new WeirReplay(recording)).GetHandoffStatusAsync(Connection, IdOfPath(recording.Exchanges[0].Request.Path), CancellationToken.None);

        Assert.Equal(401, recording.Exchanges[0].Reply.Status);
        Assert.Equal(ProcessorAnswer.Forbidden, status.Answer);
        Assert.False(string.IsNullOrWhiteSpace(status.Message));
    }

    [Fact]
    public async Task A_cancel_Weir_refuses_for_its_secret_is_forbidden()
    {
        var recording = Rec("cancel-wrong-secret");
        var result = await Service(new WeirReplay(recording)).CancelHandoffAsync(Connection, IdOfPath(recording.Exchanges[0].Request.Path), CancellationToken.None);

        Assert.Equal(401, recording.Exchanges[0].Reply.Status);
        Assert.Equal(ProcessorAnswer.Forbidden, result.Answer);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task An_outcome_Weir_refuses_for_its_secret_is_forbidden_and_so_stays_owed()
    {
        var recording = Rec("outcome-wrong-secret");
        var result = await ReportOutcome(recording.Exchanges[0], new WeirReplay(recording));

        Assert.Equal(401, recording.Exchanges[0].Reply.Status);
        Assert.Equal(ProcessorAnswer.Forbidden, result.Answer);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task A_cancel_for_a_hand_off_Weir_never_received_is_not_found()
    {
        var recording = Rec("cancel-unknown-id");
        var result = await Service(new WeirReplay(recording)).CancelHandoffAsync(Connection, IdOfPath(recording.Exchanges[0].Request.Path), CancellationToken.None);

        Assert.Equal(404, recording.Exchanges[0].Reply.Status);
        Assert.Equal(ProcessorAnswer.NotFound, result.Answer);
    }

    [Theory]
    [InlineData("status-unknown-id")]
    [InlineData("status-malformed-id")]
    public async Task A_status_poll_for_an_id_Weir_does_not_know_is_not_found_whatever_the_id_looks_like(string state)
    {
        var recording = Rec(state);
        foreach (var exchange in recording.Exchanges)
        {
            var status = await Service(new WeirReplay(recording)).GetHandoffStatusAsync(Connection, Uri.UnescapeDataString(exchange.Request.Path.Split('/').Last()), CancellationToken.None);

            Assert.Equal(404, exchange.Reply.Status);
            Assert.Equal(ProcessorAnswer.NotFound, status.Answer);
        }
    }

    [Fact]
    public async Task An_outcome_for_a_hand_off_Weir_never_received_is_not_found()
    {
        var recording = Rec("outcome-unknown-id");
        var result = await ReportOutcome(recording.Exchanges[0], new WeirReplay(recording));

        Assert.Equal(404, recording.Exchanges[0].Reply.Status);
        Assert.Equal(ProcessorAnswer.NotFound, result.Answer);
    }

    [Fact]
    public async Task A_cancel_of_a_finished_hand_off_is_busy_with_Weirs_words()
    {
        var recording = Rec("cancel-completed");
        var result = await Service(new WeirReplay(recording)).CancelHandoffAsync(Connection, IdOfPath(recording.Exchanges[0].Request.Path), CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Busy, result.Answer);
        Assert.Equal("This hand-off is completed, so Weir did not cancel it.", result.Message);
    }

    [Fact]
    public async Task A_status_poll_under_a_source_Weir_has_no_connection_for_is_forbidden_with_Weirs_instruction()
    {
        var recording = Rec("status-no-connection-for-source");
        var connection = Connection with { SubmissionUrl = "http://weir:8788/api/v1/intake/webhook/radarr" };

        var status = await Service(new WeirReplay(recording)).GetHandoffStatusAsync(connection, IdOfPath(recording.Exchanges[0].Request.Path), CancellationToken.None);

        Assert.Equal(403, recording.Exchanges[0].Reply.Status);
        Assert.Equal(ProcessorAnswer.Forbidden, status.Answer);
    }

    /// <summary>
    /// Weir 1.0.0-rc.18 with no Deluno connection left in it (its secret gone): the status poll on Deluno's own source is 403 with Weir's instruction,
    /// with Deluno's old secret and with none, and Deluno reads each as Forbidden, as it reads the answer under a source Weir has no secret for.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_status_poll_on_Deluno_own_source_once_no_connection_has_a_secret_is_forbidden_with_Weirs_instruction(int exchange)
    {
        var recording = Rec("status-deluno-source-no-secret");
        var status = await Service(new WeirReplay(recording)).GetHandoffStatusAsync(Connection, IdOfPath(recording.Exchanges[exchange].Request.Path), CancellationToken.None);

        Assert.Equal(403, recording.Exchanges[exchange].Reply.Status);
        Assert.Equal("Set a webhook secret in Weir so a media manager can ask about hand-offs.", Body(recording.Exchanges[exchange].Reply.Body)["detail"]!.GetValue<string>());
        Assert.Equal(ProcessorAnswer.Forbidden, status.Answer);
    }

    /// <summary>
    /// Weir checks the body before the secret: a wrong word, a missing member and a body that is not JSON all get 422, a body that
    /// is not declared JSON gets 415, and none of them is a "not now". Deluno reads each as a report that was wrong, so it is not
    /// sent again (the same report would be wrong again).
    /// </summary>
    [Theory]
    [InlineData(0, 422)]
    [InlineData(1, 422)]
    [InlineData(2, 422)]
    [InlineData(3, 422)]
    [InlineData(4, 415)]
    public async Task An_outcome_body_Weir_refuses_is_a_refused_report_not_a_report_owed_again(int exchange, int weirStatus)
    {
        var recording = Rec("outcome-malformed");
        var recorded = recording.Exchanges[exchange];
        Assert.Equal(weirStatus, recorded.Reply.Status);

        // Deluno never sends a body Weir would refuse, so the client is shown only the reply: the same address, answered with that reply.
        var canned = new Recording("weir", "1.0.0-rc.13", "outcome-malformed", [new Exchange(new RecordedRequest("POST", recording.Exchanges[0].Request.Path, null), recorded.Reply)]);
        var result = await Service(new WeirReplay(canned)).ReportOutcomeAsync(
            Connection, IdOfPath(recorded.Request.Path.Replace("/outcome", string.Empty)), ProcessorHandoffOutcome.Imported, "/x.mkv", null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Refused, result.Answer);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task A_change_with_an_Origin_and_no_X_Requested_With_is_refused_403_and_read_as_forbidden()
    {
        var recording = Rec("origin-without-requested-with");

        Assert.All(recording.Exchanges, e => Assert.Equal(403, e.Reply.Status));
        var cancel = await Service(new WeirReplay(recording)).CancelHandoffAsync(Connection, IdOfPath(recording.Exchanges[1].Request.Path), CancellationToken.None);
        Assert.Equal(ProcessorAnswer.Forbidden, cancel.Answer);
    }

    [Fact]
    public void A_source_key_Weir_does_not_know_is_answered_404_before_the_secret_is_looked_at()
    {
        var recording = Rec("status-unknown-source");

        Assert.Equal(2, recording.Exchanges.Count);
        Assert.All(recording.Exchanges, e => Assert.Equal(404, e.Reply.Status));
        Assert.Contains("Unknown media manager source", recording.Exchanges[1].Reply.Body);
    }

    // ----- helpers -----

    private static string IdOfPath(string path) => Uri.UnescapeDataString(Regex.Match(path, @"/handoffs/[^/]+/([^/]+)").Groups[1].Value);

    /// <summary>The recorded request's headers against the ones Deluno sent: the secret is there, the key is the hand-off id and a time.</summary>
    private static void AssertHeaders(RecordedRequest recorded, SentRequest sent, bool expectIdempotencyKey)
    {
        Assert.NotNull(recorded.Headers);
        Assert.True(recorded.Headers!.ContainsKey("x-webhook-secret"), "The recording lost the secret header.");
        Assert.Equal(Secret, sent.Headers["X-Webhook-Secret"]);
        if (expectIdempotencyKey)
        {
            var recordedKey = recorded.Headers["idempotency-key"];
            Assert.Matches(@"^[0-9a-f]{32}:\d+$", recordedKey);
            Assert.StartsWith(recordedKey.Split(':')[0] + ":", sent.Headers["Idempotency-Key"]);
            Assert.Matches(@"^[0-9a-f]{32}:\d+$", sent.Headers["Idempotency-Key"]);
        }
        else
        {
            Assert.False(recorded.Headers.ContainsKey("idempotency-key"), "A call that is not a hand-off carries no Idempotency-Key.");
            Assert.False(sent.Headers.ContainsKey("Idempotency-Key"));
        }
    }

    private static Task<ProcessorSubmissionResult> Submit(Exchange recorded, WeirReplay replay)
    {
        var body = Body(recorded.Request.Body).AsObject();
        var resolver = new Mock<IProcessorHandoffTitleResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<ProcessorHandoffItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(body["title"] is null ? null : new ProcessorHandoffTitle(body["title"]!.GetValue<string>(), body["year"]?.GetValue<int>()));
        var handoff = WeirContractClientTests.Handoff(
            body["handoffId"]!.GetValue<string>(),
            body["libraryId"]!.GetValue<string>(),
            body["mediaType"]!.GetValue<string>(),
            body["sourcePath"]!.GetValue<string>(),
            body["releaseName"]!.GetValue<string>(),
            body["queueItemId"]!.GetValue<string>(),
            body["sourceFiles"]?.AsArray().Select(f => f!.GetValue<string>()).ToArray());
        return Service(replay, resolver.Object).SubmitAsync(Connection, handoff, CancellationToken.None);
    }

    private static Task<ProcessorOutcomeResult> ReportOutcome(Exchange recorded, WeirReplay replay)
    {
        var sent = Body(recorded.Request.Body).AsObject();
        var imported = sent["outcome"]!.GetValue<string>() == "imported";
        return Service(replay).ReportOutcomeAsync(
            Connection,
            Regex.Match(recorded.Request.Path, @"/handoffs/deluno/([^/]+)/outcome").Groups[1].Value,
            imported ? ProcessorHandoffOutcome.Imported : ProcessorHandoffOutcome.NotImported,
            imported ? sent["importedPath"]?.GetValue<string>() : "/offered/x.mkv",
            sent["reason"]?.GetValue<string>(),
            DateTimeOffset.Parse(sent["occurredUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            CancellationToken.None);
    }
}
