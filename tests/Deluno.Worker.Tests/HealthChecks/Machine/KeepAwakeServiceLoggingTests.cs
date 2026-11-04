using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Machine;
using Deluno.Worker.Tests.Support;
using Microsoft.Extensions.Logging;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Machine;

/// <summary>A setting that cannot be read is skipped quietly, but written to the debug log with its error.</summary>
public sealed class KeepAwakeServiceLoggingTests
{
    [Fact]
    public async Task A_setting_that_cannot_be_read_is_logged_for_debugging_with_the_error()
    {
        var broken = new IOException("the settings file is being written");
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(broken);
        var log = new RecordingLogger<KeepAwakeService>();
        var service = new KeepAwakeService(settings.Object, log, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(), () => true, _ => 0u);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => log.Entries.Count > 0, "the failed read to be logged");
        await service.StopAsync(CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("Could not ask Windows to stay awake.", entry.Message);
        Assert.Same(broken, entry.Exception);
    }
}
