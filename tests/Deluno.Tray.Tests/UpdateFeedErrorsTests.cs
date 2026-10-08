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
