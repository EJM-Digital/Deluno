using Deluno.Notifications.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoNotificationsModule(this IServiceCollection services)
    {
        services.AddSingleton<INotificationRepository, SqliteNotificationRepository>();
        services.AddSingleton<IOutboundNotificationService, OutboundNotificationService>();
        services.AddSingleton<INotificationService, InMemoryNotificationService>();
        services.AddHttpClient("notifications");

        // Registered as itself, not only as IHostedService, so job handlers
        // that fire notifications (subtitle search among them, #839) can
        // inject it directly rather than through the hosted-service surface,
        // which offers only StartAsync/StopAsync.
        services.AddSingleton<NotificationEventPublisher>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<NotificationEventPublisher>());
        return services;
    }
}
