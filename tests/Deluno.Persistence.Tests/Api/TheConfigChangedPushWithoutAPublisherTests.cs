using Deluno.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A host with no realtime publisher (a tool that maps the API without the live channel) still answers every write: the
/// push is skipped, never a failure, for a Settings area, for a screen that follows its own event, and for a path outside both.
/// </summary>
public sealed class TheConfigChangedPushWithoutAPublisherTests
{
    [Theory]
    [InlineData("POST", "/api/tags/", 200)]
    [InlineData("POST", "/api/movie-collections/", 200)]
    [InlineData("PUT", "/api/not-a-settings-area/1", 204)]
    [InlineData("POST", "/health", 200)]
    [InlineData("GET", "/api/tags/", 200)]
    [InlineData("POST", "/api/tags/", 500)]
    [InlineData("POST", "/api/tags/", 199)]
    [InlineData("HEAD", "/api/tags/", 200)]
    [InlineData("OPTIONS", "/api/tags/", 204)]
    [InlineData("DELETE", "/api/tags/1", 204)]
    public async Task A_write_is_answered_as_it_was_when_nothing_is_listening(string method, string path, int status)
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseDelunoConfigChangedPush();
        app.Run(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Method = method;
        context.Request.Path = path;

        await pipeline(context);

        Assert.Equal(status, context.Response.StatusCode);
    }
}
