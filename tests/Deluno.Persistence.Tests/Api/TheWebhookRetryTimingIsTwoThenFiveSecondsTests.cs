using System.Reflection;
using Deluno.Notifications;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Coverage register (#1329), external integration API: a webhook delivery is tried three times in all, the first at once, the
/// second 2 s later and the third 5 s after that. The existing test only asserts "two attempts, the second succeeds"; this holds the
/// timing the retry loop and the "next attempt" it reports are built from.
/// </summary>
public sealed class TheWebhookRetryTimingIsTwoThenFiveSecondsTests
{
    [Fact]
    public void A_delivery_is_tried_at_once_then_after_two_seconds_then_after_five_and_never_a_fourth_time()
    {
        var delays = (TimeSpan[])typeof(OutboundNotificationService)
            .GetField("RetryDelays", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], delays);
    }
}
