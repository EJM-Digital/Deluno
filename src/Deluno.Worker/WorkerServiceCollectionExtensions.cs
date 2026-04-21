using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Worker;

public static class WorkerServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoWorkerModule(this IServiceCollection services)
    {
        services.AddHostedService<DelunoHeartbeatWorker>();
        return services;
    }
}

