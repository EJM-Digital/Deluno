using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>Asking for the bell to be refreshed never fails the work that asked, even when the push cannot be sent (#1329).</summary>
public sealed class RequestAttentionChangedLowGapTests
{
    private sealed class RefusingPublisher : NullRealtimeEventPublisher, IRealtimeEventPublisher
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        Task IRealtimeEventPublisher.PublishDashboardEventAsync(string eventName, object payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("The hub is not ready.");
        }
    }

    [Fact]
    public async Task A_push_that_cannot_be_sent_is_dropped_without_an_error_and_the_next_request_is_tried_again()
    {
        var publisher = new RefusingPublisher();

        publisher.RequestAttentionChanged("noRelease");
        await Wait.UntilAsync(() => publisher.Attempts == 1, "the first refused push to be attempted");
        publisher.RequestAttentionChanged("recovery");
        await Wait.UntilAsync(() => publisher.Attempts == 2, "the second push to be attempted after the first was dropped");

        Assert.Equal(2, publisher.Attempts);
    }
}
