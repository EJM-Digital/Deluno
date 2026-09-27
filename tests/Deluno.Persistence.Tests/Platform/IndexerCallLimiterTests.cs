using Deluno.Connections;
using Deluno.Connections.Contracts;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// Per-indexer Query Limit / Grab Limit caps (#750): a hard ceiling on
/// searches or grabs per day or per hour, in addition to the existing
/// request-interval pacing.
/// </summary>
public sealed class IndexerCallLimiterTests
{
    private static IndexerItem Indexer(int? queryLimit = null, int? grabLimit = null, string? limitsUnit = null) =>
        new(
            Id: "idx-1",
            Name: "Private Tracker",
            Protocol: "torznab",
            Privacy: "private",
            BaseUrl: "https://tracker.example.test",
            ApiKey: "key",
            Priority: 10,
            Categories: "2000",
            Tags: "",
            MediaScope: "movies",
            IsEnabled: true,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: null,
            ConsecutiveFailures: 0,
            RateLimitedUntilUtc: null,
            DisabledReason: null,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow)
        {
            QueryLimit = queryLimit,
            GrabLimit = grabLimit,
            LimitsUnit = limitsUnit
        };

    [Fact]
    public void TryConsumeQuery_is_unrestricted_when_no_limit_is_configured()
    {
        var limiter = new IndexerCallLimiter();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        for (var i = 0; i < 50; i++)
        {
            Assert.True(limiter.TryConsumeQuery(Indexer(), now).Allowed);
        }
    }

    [Fact]
    public void TryConsumeQuery_blocks_once_the_limit_is_reached_within_the_window()
    {
        var limiter = new IndexerCallLimiter();
        var indexer = Indexer(queryLimit: 3, limitsUnit: "day");
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        Assert.True(limiter.TryConsumeQuery(indexer, now).Allowed);
        Assert.True(limiter.TryConsumeQuery(indexer, now).Allowed);
        Assert.True(limiter.TryConsumeQuery(indexer, now).Allowed);

        var fourth = limiter.TryConsumeQuery(indexer, now);
        Assert.False(fourth.Allowed);
        Assert.Equal(3, fourth.Limit);
        Assert.Equal("day", fourth.Unit);
        Assert.Equal(now.AddDays(1), fourth.ResetsAtUtc);
    }

    [Fact]
    public void TryConsumeQuery_count_resets_at_the_next_window_boundary()
    {
        var limiter = new IndexerCallLimiter();
        var indexer = Indexer(queryLimit: 1, limitsUnit: "hour");
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        Assert.True(limiter.TryConsumeQuery(indexer, now).Allowed);
        Assert.False(limiter.TryConsumeQuery(indexer, now.AddMinutes(30)).Allowed);

        // Past the hour boundary, the window rolls over and the count
        // starts fresh, allowing exactly one more call.
        var afterBoundary = limiter.TryConsumeQuery(indexer, now.AddHours(1).AddSeconds(1));
        Assert.True(afterBoundary.Allowed);
        Assert.False(limiter.TryConsumeQuery(indexer, now.AddHours(1).AddSeconds(2)).Allowed);
    }

    [Fact]
    public void Grab_and_query_limits_are_counted_separately()
    {
        var limiter = new IndexerCallLimiter();
        var indexer = Indexer(queryLimit: 1, grabLimit: 1, limitsUnit: "day");
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        Assert.True(limiter.TryConsumeQuery(indexer, now).Allowed);
        Assert.False(limiter.TryConsumeQuery(indexer, now).Allowed);

        // The grab cap is untouched by the query cap being reached.
        Assert.True(limiter.TryConsumeGrab(indexer, now).Allowed);
        Assert.False(limiter.TryConsumeGrab(indexer, now).Allowed);
    }

    [Fact]
    public void Peek_reports_state_without_consuming_a_call()
    {
        var limiter = new IndexerCallLimiter();
        var indexer = Indexer(queryLimit: 2, limitsUnit: "day");
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        limiter.TryConsumeQuery(indexer, now);
        var peeked = limiter.Peek(indexer, IndexerCallKind.Query, now);

        Assert.True(peeked.Allowed);
        Assert.Equal(1, peeked.Count);
        // Peeking again does not advance the count.
        Assert.Equal(1, limiter.Peek(indexer, IndexerCallKind.Query, now).Count);
    }

    [Fact]
    public void Default_unit_is_day_when_not_configured()
    {
        var limiter = new IndexerCallLimiter();
        var indexer = Indexer(queryLimit: 1);
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var result = limiter.TryConsumeQuery(indexer, now);
        Assert.Equal("day", result.Unit);
        Assert.Equal(now.AddDays(1), result.ResetsAtUtc);
    }
}
