using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Realtime;

public static class RealtimeServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoRealtimeModule(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<SignalRRealtimeEventPublisher>();
        services.AddSingleton<IRealtimeEventPublisher>(provider => provider.GetRequiredService<SignalRRealtimeEventPublisher>());
        services.AddHostedService(provider => provider.GetRequiredService<SignalRRealtimeEventPublisher>());
        return services;
    }

    public static IEndpointRouteBuilder MapDelunoRealtime(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<ActivityHub>("/hubs/activity");
        endpoints.MapHub<ActivityHub>("/hubs/deluno");
        return endpoints;
    }
}
