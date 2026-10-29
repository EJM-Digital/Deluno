using Deluno.Api;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1413: a live-update push that throws after a write was saved is logged and swallowed; the write is answered as saved.
/// </summary>
public sealed class ATheConfigChangedPushThatThrowsKeepsTheSavedWriteTests
{
    private sealed class ThrowingPublisher : NullRealtimeEventPublisher, IRealtimeEventPublisher
    {
        public override Task PublishEntityChangedAsync(string entityType, string entityId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("push failed");

        Task IRealtimeEventPublisher.PublishDashboardEventAsync(string eventName, object payload, CancellationToken cancellationToken)
            => throw new InvalidOperationException("push failed");
    }

    [Theory]
    [InlineData("/api/tags/")]
    [InlineData("/api/movie-collections/")]
    public async Task A_write_that_was_saved_is_answered_as_saved_when_the_push_throws(string path)
    {
        var saved = false;
        var services = new ServiceCollection().AddSingleton<IRealtimeEventPublisher>(new ThrowingPublisher()).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseDelunoConfigChangedPush();
        app.Run(context =>
        {
            saved = true;
            context.Response.StatusCode = 201;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Path = path;

        await app.Build()(context);

        Assert.True(saved);
        Assert.Equal(201, context.Response.StatusCode);
    }
}
