using Deluno.Contracts;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Realtime;

public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// How long the server waits for a client's ping before it drops the connection (#937).
    ///
    /// Chrome throttles a hidden, occluded, or minimised tab's own chained
    /// <c>setInterval</c>/<c>setTimeout</c> timers to at most one firing a
    /// minute after roughly five minutes
    /// (https://developer.chrome.com/blog/background_tabs/). The SignalR
    /// JS client's own keep-alive ping runs on exactly that kind of page
    /// timer, so the framework default (30 s) drops a connection the moment
    /// a background tab's ping is delayed past one throttled minute --
    /// which then reconnects and replays or resyncs: the catch-up #937
    /// exists to remove. 90 seconds tolerates one full throttled cycle plus
    /// jitter without the connection being torn down.
    ///
    /// This alone would blur a real outage with a healthy-but-throttled tab
    /// for up to 90 s. The client's own worker-driven nudge (a periodic
    /// <c>Ping</c> invoke off a dedicated Worker's clock, not a page timer --
    /// see apps/web/src/lib/use-signalr.tsx) is not subject to that
    /// throttling at all, so it keeps arriving on schedule regardless of
    /// visibility. A real outage (network down, server unreachable, tab
    /// actually closed) still stops both kinds of traffic and is still
    /// caught within this window.
    /// </summary>
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How often the server itself pings an idle connection (#937). Left at
    /// roughly a third of <see cref="ClientTimeout"/>, as SignalR recommends,
    /// so at least two server pings are missed before the client is dropped.
    /// </summary>
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddDelunoRealtimeModule(this IServiceCollection services)
    {
        services.AddSignalR(options =>
        {
            options.ClientTimeoutInterval = ClientTimeout;
            options.KeepAliveInterval = KeepAlive;
        });
        services.AddSingleton<RealtimeWatcherTracker>();
        services.AddSingleton<IRealtimeWatchers>(provider => provider.GetRequiredService<RealtimeWatcherTracker>());
        services.AddSingleton<SignalRRealtimeEventPublisher>();
        services.AddSingleton<IRealtimeEventPublisher>(provider => provider.GetRequiredService<SignalRRealtimeEventPublisher>());
        services.AddSingleton<IRealtimeResumeSource>(provider => provider.GetRequiredService<SignalRRealtimeEventPublisher>());
        services.AddHostedService(provider => provider.GetRequiredService<SignalRRealtimeEventPublisher>());
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoRealtime(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<ActivityHub>("/hubs/deluno");
        return endpoints;
    }
}
