using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Processors;

/// <summary>
/// #1301: what happens when Deluno hands Weir 1.0.0-rc.13 a finished download, against Weir's own recorded replies: the hand-off
/// it takes, the one it refuses because no workflow watches that folder (Weir's reason kept, in Weir's words), and the one it
/// refuses for a wrong secret.
/// </summary>
public sealed class HandingAFileToWeirOnRecordingsTests
{
    private static readonly ProcessorConnectionItem Weir = new(
        "connection-1", "Weir", "generic-webhook", "http://weir:8788/api/v1/intake/webhook/deluno",
        "X-Webhook-Secret", "shared-secret", true, "healthy", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static readonly ProcessorHandoffItem Handoff = new(
        "handoff-1", "library-1", "movies", "client-1", "queue-1", "Recorded elsewhere",
        "C:\\golden\\recorded\\weir\\Recorded.Weir.NW.mkv", "Weir", "waiting", null, null, null,
        DateTimeOffset.Parse("2026-10-11T09:00:00Z"), DateTimeOffset.Parse("2026-10-11T09:00:00Z"));

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task<(ProcessorSubmissionResult Result, List<string> Sent)> SubmitAnsweredBy(string recording)
    {
        var sent = new List<string>();
        var handler = RecordedReplies.Handler("weir", [RecordedReplies.Weir(recording)], (request, body) =>
        {
            sent.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {body}");
            return null;
        });
        var result = await new ProcessorConnectionService(new Factory(handler)).SubmitAsync(Weir, Handoff, CancellationToken.None);
        return (result, sent);
    }

    [Fact]
    public async Task A_hand_off_for_a_file_in_no_watched_folder_is_refused_with_weirs_own_reason_and_its_status()
    {
        var recorded = RecordedReplies.Weir("handoff-refused-not-watched").Single.Reply;
        Assert.Equal(400, recorded.Status);
        var detail = recorded.Json()["detail"]!.GetValue<string>();

        var (result, sent) = await SubmitAnsweredBy("handoff-refused-not-watched");

        Assert.False(result.IsAccepted);
        Assert.Contains(detail, result.Message, StringComparison.Ordinal);
        Assert.Contains("400", result.Message, StringComparison.Ordinal);
        Assert.Contains("POST /api/v1/intake/webhook/deluno", Assert.Single(sent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hand_off_Weir_takes_is_accepted_and_the_secret_goes_in_the_header_Weir_reads_it_from()
    {
        var accepted = RecordedReplies.Weir("handoff-accepted").Exchanges.First(exchange => exchange.Request.Method == "POST").Reply;
        var seenSecret = (string?)null;
        var handler = RecordedReplies.Handler("weir", [RecordedReplies.Weir("handoff-accepted")], (request, _) =>
        {
            seenSecret = request.Headers.TryGetValues("X-Webhook-Secret", out var values) ? values.First() : null;
            return null;
        });

        var result = await new ProcessorConnectionService(new Factory(handler)).SubmitAsync(Weir, Handoff, CancellationToken.None);

        Assert.InRange(accepted.Status, 200, 299);
        Assert.True(result.IsAccepted, result.Message);
        Assert.Equal("shared-secret", seenSecret);
    }

    [Fact]
    public async Task A_hand_off_sent_with_a_secret_Weir_refuses_is_not_accepted_and_says_the_status()
    {
        var (result, _) = await SubmitAnsweredBy("handoff-refused-wrong-secret");

        Assert.False(result.IsAccepted);
        Assert.Contains("401", result.Message, StringComparison.Ordinal);
    }
}
