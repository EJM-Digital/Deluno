using Deluno.Notifications.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoNotificationsModule(this IServiceCollection services)
    {
        services.AddSingleton<INotificationRepository, SqliteNotificationRepository>();
        services.AddSingleton<IOutboundNotificationService, OutboundNotificationService>();
        services.AddSingleton<INotificationService, SqliteNotificationService>();
        services.AddHttpClient("notifications");
        return services;
    }
}
