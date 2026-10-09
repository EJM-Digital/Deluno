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
    private sealed class Answer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    [Fact]
    public void A_403_or_429_or_the_words_rate_limit_are_GitHubs_limit_and_a_404_or_other_failure_is_not()
    {
        Assert.True(UpdateFeedErrors.IsRateLimited(new HttpRequestException("no", null, HttpStatusCode.Forbidden)));
        Assert.True(UpdateFeedErrors.IsRateLimited(new InvalidOperationException("Failed to fetch releases", new HttpRequestException("no", null, HttpStatusCode.TooManyRequests))));
        Assert.True(UpdateFeedErrors.IsRateLimited(new HttpRequestException("Response status code does not indicate success: 403 (rate limit exceeded).")));
        Assert.False(UpdateFeedErrors.IsRateLimited(new HttpRequestException("gone", null, HttpStatusCode.NotFound)));
        Assert.False(UpdateFeedErrors.IsRateLimited(new IOException("disk full")));
    }

    [Fact]
    public void The_message_names_the_limit_and_gives_the_reset_as_a_UTC_stamp_the_web_page_shows_in_the_viewers_time()
    {
        var limit = new HttpRequestException("no", null, HttpStatusCode.Forbidden);
        var at = new DateTimeOffset(2026, 10, 8, 20, 21, 0, TimeSpan.Zero);

        var text = UpdateFeedErrors.CheckFailedMessage(limit, at);

        Assert.StartsWith("Couldn't check for updates (GitHub limit, retry after ", text);
        Assert.Contains("retry after 2026-10-08 20:21 UTC", text);
        Assert.DoesNotContain(at.ToLocalTime().ToString("h:mm tt") + ")", text.Replace("UTC)", ""));
        Assert.Contains("GitHub limit", UpdateFeedErrors.CheckFailedMessage(limit, null));
        Assert.DoesNotContain("GitHub", UpdateFeedErrors.CheckFailedMessage(new IOException("x"), null));
    }

    [Fact]
    public async Task The_reset_time_is_read_from_GitHubs_free_rate_limit_call()
    {
        var reset = new DateTimeOffset(2026, 10, 8, 20, 21, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var at = await UpdateFeedErrors.ReadLimitResetAsync(new Answer("{\"resources\":{\"core\":{\"limit\":60,\"remaining\":0,\"reset\":" + reset + "}}}"), CancellationToken.None);

        Assert.Equal(reset, at!.Value.ToUnixTimeSeconds());
        Assert.Null(await UpdateFeedErrors.ReadLimitResetAsync(new Answer("not json"), CancellationToken.None));
    }
}
