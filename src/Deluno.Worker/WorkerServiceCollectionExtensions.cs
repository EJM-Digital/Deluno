using Deluno.Worker.Services;
using Deluno.Worker.Intake;
using Deluno.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Worker;

public static class WorkerServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoWorkerModule(this IServiceCollection services)
    {
        services.AddHttpClient("deluno-intake", client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IntakeSyncService>();
        services.AddScoped<IIntakeSyncService>(serviceProvider => serviceProvider.GetRequiredService<IntakeSyncService>());
        services.AddScoped<IIntakeListPreviewService>(serviceProvider => serviceProvider.GetRequiredService<IntakeSyncService>());
        services.AddScoped<IIntakeListApprovalService>(serviceProvider => serviceProvider.GetRequiredService<IntakeSyncService>());
        services.AddHostedService<DelunoHeartbeatWorker>();
        return services;
    }
}
