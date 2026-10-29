using Deluno.Contracts;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>How long Deluno waits before asking a media server again after each failed try (#1329).</summary>
public sealed class MediaServerRefreshBackoffLowGapTests
{
    [Fact]
    public void Each_failed_attempt_waits_longer_than_the_last_and_stays_at_two_hours()
    {
        var waits = Enumerable.Range(1, 9).Select(MediaServerRefreshBackoff.Next).ToArray();

        Assert.Equal(
            [
                TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2),
                TimeSpan.FromHours(2), TimeSpan.FromHours(2), TimeSpan.FromHours(2)
            ],
            waits);
        Assert.Equal(TimeSpan.FromMinutes(2), MediaServerRefreshBackoff.Next(0));
    }
}
