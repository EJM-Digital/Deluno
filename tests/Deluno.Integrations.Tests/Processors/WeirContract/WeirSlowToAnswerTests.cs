using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;
using static Deluno.Integrations.Tests.Processors.WeirContract.WeirContract;

namespace Deluno.Integrations.Tests.Processors.WeirContract;

/// <summary>
/// #1301: Weir slow to answer (for example while it restarts after an update). The first call is not answered before the
/// client's own time limit; the next is answered with Weir's recorded reply. Deluno says it could not reach Weir, holds
/// nothing against the hand-off, and the next ask gets the real answer. The time limit here is the HttpClient's, made short
/// so the test does not wait for the product's ten seconds; the replies are all Weir's recordings.
/// </summary>
public sealed class WeirSlowToAnswerTests
{
    private sealed class SlowFirst(HttpMessageHandler inner, int slowCalls) : DelegatingHandler(inner)
    {
        private int _calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) <= slowCalls)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); // reason: Weir does not answer; the wait ends when the client's own time limit cancels this call
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static ProcessorConnectionService SlowService(WeirReplay replay)
        => new(new Factory(new HttpClient(new SlowFirst(replay, 1)) { Timeout = TimeSpan.FromMilliseconds(300) }));

    [Fact]
    public async Task A_status_poll_Weir_is_too_slow_to_answer_is_unreachable_and_the_next_poll_gets_the_real_status()
    {
        var recording = Rec("status-working");
        var service = SlowService(new WeirReplay(recording));
        var id = HandoffIdOf(recording);

        var slow = await service.GetHandoffStatusAsync(Connection, id, CancellationToken.None);
        var next = await service.GetHandoffStatusAsync(Connection, id, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unreachable, slow.Answer);
        Assert.Equal("Deluno could not reach Weir.", slow.Message);
        Assert.Equal(ProcessorAnswer.Answered, next.Answer);
    }

    [Fact]
    public async Task A_cancel_Weir_is_too_slow_to_answer_is_unreachable_so_the_hand_off_is_not_given_up_on_and_asking_again_works()
    {
        var recording = Rec("handoff-cancelled");
        var service = SlowService(new WeirReplay(recording));
        var id = HandoffIdOf(recording);

        var slow = await service.CancelHandoffAsync(Connection, id, CancellationToken.None);
        var next = await service.CancelHandoffAsync(Connection, id, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unreachable, slow.Answer);
        Assert.Equal("Deluno could not reach Weir.", slow.Message);
        Assert.Equal(ProcessorAnswer.Answered, next.Answer);
    }

    [Fact]
    public async Task The_capabilities_Weir_is_too_slow_to_give_are_unreachable_and_the_next_ask_says_what_Weir_can_do()
    {
        var service = SlowService(new WeirReplay(Rec("capabilities")));

        var slow = await service.GetStatusSupportAsync(Connection, CancellationToken.None);
        var next = await service.GetStatusSupportAsync(Connection, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unreachable, slow.Answer);
        Assert.False(slow.CanReportStatus);
        Assert.Equal("Deluno could not reach Weir.", slow.Message);
        Assert.Equal(ProcessorAnswer.Answered, next.Answer);
        Assert.True(next.CanReportStatus);
    }

    [Fact]
    public async Task A_name_asked_of_a_Weir_too_slow_to_answer_is_none_and_the_next_ask_gets_it()
    {
        var service = SlowService(new WeirReplay(Rec("capabilities")));

        var slow = await service.GetProductNameAsync(Connection.SubmissionUrl, Connection.AuthHeaderName, Secret, CancellationToken.None);
        var next = await service.GetProductNameAsync(Connection.SubmissionUrl, Connection.AuthHeaderName, Secret, CancellationToken.None);

        Assert.Null(slow);
        Assert.Equal("Weir", next);
    }
}
