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
        // #1364: the client's own request log line carries the whole address, and a Discord or Slack address holds its token in the path.
        // The service logs the host alone.
        services.AddHttpClient("notifications").RemoveAllLoggers();
        return services;
    }
}
