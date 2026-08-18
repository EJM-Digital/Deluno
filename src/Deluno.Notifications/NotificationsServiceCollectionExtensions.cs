using Deluno.Notifications.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoNotificationsModule(this IServiceCollection services)
    {
        services.AddSingleton<INotificationRepository, SqliteNotificationRepository>();
        services.AddSingleton<IOutboundNotificationService, OutboundNotificationService>();
        services.AddSingleton<INotificationService, InMemoryNotificationService>();
        services.AddHttpClient("notifications");
        services.AddHostedService<NotificationEventPublisher>();
        return services;
    }
}
