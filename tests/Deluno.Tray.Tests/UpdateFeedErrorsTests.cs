using System.Net;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>A fresh install's update check against a feed that is not published yet is "no update", not a logged failure.</summary>
public sealed class UpdateFeedErrorsTests
{
    [Fact]
    public void A_404_from_the_feed_is_not_an_error()
    {
        Assert.True(UpdateFeedErrors.IsNotFound(new HttpRequestException("gone", null, HttpStatusCode.NotFound)));
        Assert.True(UpdateFeedErrors.IsNotFound(new InvalidOperationException("Failed to fetch releases", new HttpRequestException("gone", null, HttpStatusCode.NotFound))));
        Assert.True(UpdateFeedErrors.IsNotFound(new HttpRequestException("Response status code does not indicate success: 404 (Not Found).")));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public void Any_other_failure_is_still_a_failure(HttpStatusCode status)
    {
        Assert.False(UpdateFeedErrors.IsNotFound(new HttpRequestException("failed", null, status)));
        Assert.False(UpdateFeedErrors.IsNotFound(new TimeoutException("timed out")));
        Assert.False(UpdateFeedErrors.IsNotFound(null));
    }
}

// Golden path, rc.30.8: the check failed on GitHub's anonymous limit, and the Updates page still said "Up to date".
public sealed class UpdateCheckRateLimitTests
{
    [Fact]
    public void The_limit_is_the_shared_handlers_own_sentence_with_the_reset_as_a_UTC_stamp_the_web_page_shows_in_the_viewers_time()
    {
        var at = new DateTimeOffset(2026, 10, 8, 20, 21, 0, TimeSpan.Zero);
        var limit = new InvalidOperationException("Failed to fetch releases", new Deluno.Infrastructure.Http.GitHubRateLimitException(at));

        var text = UpdateFeedErrors.CheckFailedMessage(limit);

        Assert.Equal(Deluno.Infrastructure.Http.GitHubRateLimitException.MessageFor(at), text);
        Assert.Contains("2026-10-08 20:21 UTC", text);
        // A bare 403 the shared handler did not recognise is no longer guessed to be the limit.
        Assert.StartsWith("Couldn't check for updates right now", UpdateFeedErrors.CheckFailedMessage(new HttpRequestException("no", null, HttpStatusCode.Forbidden)));
    }

    [Fact]
    public void A_feed_that_answers_404_is_a_check_that_could_not_be_made_never_up_to_date()
    {
        var text = UpdateFeedErrors.CheckFailedMessage(new HttpRequestException("gone", null, HttpStatusCode.NotFound));

        Assert.Equal("Couldn't check for updates: the update feed isn't reachable.", text);
        Assert.DoesNotContain("latest version", text);
        Assert.DoesNotContain("up to date", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Other_failures_keep_their_own_words()
        => Assert.DoesNotContain("GitHub", UpdateFeedErrors.CheckFailedMessage(new IOException("x")));
}
