using Deluno.Platform.Data;
using Deluno.Platform.Migration;
using Deluno.Platform.Notifications;
using Deluno.Platform.Quality;
using Deluno.Platform.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoPlatformModule(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformSettingsRepository, SqlitePlatformSettingsRepository>();
        services.AddSingleton<IVersionedMediaPolicyEngine, VersionedMediaPolicyEngine>();
        services.AddSingleton<IMediaDecisionService, MediaDecisionService>();
        services.AddSingleton<IMigrationAssistantService, MigrationAssistantService>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<IOutboundNotificationService, OutboundNotificationService>();
        services.AddSingleton<INotificationService, InMemoryNotificationService>();
        services.AddHttpClient("notifications");
        services.AddHostedService<PlatformSchemaInitializer>();
        services.AddHostedService<NotificationEventPublisher>();
        return services;
    }
}
