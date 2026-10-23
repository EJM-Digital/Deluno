using Deluno.Platform.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// A pass of the mislabelled-episode-file repair that cannot run (the settings cannot be read) is logged and leaves every file as it is
/// (coverage register, engines); the service stays up to try again at its next six-hourly turn and stops cleanly when the host does.
/// Refs #1296.
/// </summary>
public sealed class AFailedMislabelledFilePassDoesNotEndTheServiceTests
{
    [Fact]
    public async Task A_pass_that_throws_is_logged_and_the_service_stays_up_for_its_next_turn_and_stops_cleanly()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("The database is busy."));
        var logger = new Mock<ILogger<MislabelledEpisodeFileRepairService>>();
        logger.Setup(item => item.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        await using var provider = new ServiceCollection().AddSingleton(settings.Object).BuildServiceProvider();
        var service = new MislabelledEpisodeFileRepairService(provider.GetRequiredService<IServiceScopeFactory>(), logger.Object);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => logger.Invocations.Any(call => call.Method.Name == nameof(ILogger.Log) && Equals(call.Arguments[0], LogLevel.Warning)), "the failed pass to be logged as a warning");

        Assert.False(service.ExecuteTask is { IsCompleted: true }, "the service must stay up for its next turn");
        await service.StopAsync(CancellationToken.None);
        Assert.True(service.ExecuteTask is { IsCompletedSuccessfully: true });
        settings.Verify(repository => repository.GetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
