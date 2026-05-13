using Deluno.Worker.Services;
using Deluno.Worker.Intake;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Worker;

public static class WorkerServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoWorkerModule(this IServiceCollection services)
    {
        services.AddScoped<IIntakeSyncService, IntakeSyncService>();
        services.AddHostedService<DelunoHeartbeatWorker>();
        return services;
    }
}
