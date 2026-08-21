using Deluno.Contracts;
using Deluno.Recovery.Contracts;
using Deluno.Recovery.Policies;
using Deluno.Recovery.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Recovery;

public static class RecoveryServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoRecoveryModule(this IServiceCollection services)
    {
        services.AddSingleton<IRetryPolicyCatalog, RetryPolicyCatalog>();
        services.AddSingleton<IRecoveryHealthEvaluator, DownloadHealthEvaluator>();
        services.AddSingleton<IDispatchCleanupService, DispatchCleanupService>();
        services.AddSingleton<IDownloadRetryService, DownloadRetryService>();
        services.AddSingleton<CompositeDispatchRecoveryHandler>(provider =>
            new CompositeDispatchRecoveryHandler(provider.GetServices<IDispatchRecoveryHandlerComponent>().ToList()));
        services.AddSingleton<IDispatchRecoveryHandler>(provider =>
            provider.GetRequiredService<CompositeDispatchRecoveryHandler>());
        services.AddHostedService<ImportRecoveryRetentionService>();
        return services;
    }
}
