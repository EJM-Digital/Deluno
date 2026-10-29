using Deluno.Api.SystemInfo;
using Deluno.Contracts;
using Deluno.Infrastructure.Observability;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Deluno.Persistence.Tests.SystemLog;

/// <summary>
/// Coverage register, area services: <c>SystemLogPushService</c> pushes each new notable log line to screens watching the dashboard
/// subject (event <c>SystemLogEntry</c>), only while a screen watches, only for lines the Log card keeps, and not after it is stopped.
/// </summary>
public sealed class TheLogCardIsPushedEachNewNotableLineTests
{
    private sealed class Watchers(int count) : IRealtimeWatchers
    {
        public int Count(string subject) => subject == "dashboard" ? count : 0;
    }

    [Fact]
    public async Task A_new_warning_is_pushed_to_the_dashboard_subject_with_its_level_and_message()
    {
        var buffer = new DelunoLogBuffer();
        var publisher = new RecordingRealtimeEventPublisher();
        var service = new SystemLogPushService(buffer, publisher, new Watchers(1));
        await service.StartAsync(CancellationToken.None);

        buffer.Add(LogLevel.Warning, "Deluno.Jobs.Something", "The disk is nearly full.");

        var pushed = Assert.Single(publisher.DashboardEvents);
        Assert.Equal(SystemLogAndDatabasesEndpointRouteBuilderExtensions.LogEntryEventName, pushed.Name);
        var item = Assert.IsType<SystemLogEntry>(pushed.Payload);
        Assert.Equal("warning", item.Level);
        Assert.Equal("The disk is nearly full.", item.Message);
    }

    [Fact]
    public async Task Nothing_is_pushed_while_no_screen_watches_for_a_line_the_card_does_not_keep_or_after_the_service_stops()
    {
        var buffer = new DelunoLogBuffer();
        var publisher = new RecordingRealtimeEventPublisher();
        var nobodyWatching = new SystemLogPushService(buffer, publisher, new Watchers(0));
        await nobodyWatching.StartAsync(CancellationToken.None);
        buffer.Add(LogLevel.Error, "Deluno.Jobs.Something", "Nobody is looking.");
        Assert.Empty(publisher.DashboardEvents);
        await nobodyWatching.StopAsync(CancellationToken.None);

        var watched = new SystemLogPushService(buffer, publisher, new Watchers(2));
        await watched.StartAsync(CancellationToken.None);
        buffer.Add(LogLevel.Information, "Deluno.Jobs.Something", "An ordinary information line.");
        Assert.Empty(publisher.DashboardEvents);

        await watched.StopAsync(CancellationToken.None);
        buffer.Add(LogLevel.Error, "Deluno.Jobs.Something", "After the service stopped.");
        Assert.Empty(publisher.DashboardEvents);
    }
}
