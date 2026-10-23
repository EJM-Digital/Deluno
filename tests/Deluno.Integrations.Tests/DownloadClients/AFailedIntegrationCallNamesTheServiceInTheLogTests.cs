using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Two download clients that both stop answering used to write the same log line
/// ("Integration operation telemetry failed after 3 attempts."), so the System
/// screen's log read as one sentence said twice. The line now names the service.
/// </summary>
public sealed class AFailedIntegrationCallNamesTheServiceInTheLogTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-resilience-log-tests", Guid.NewGuid().ToString("N"));

    public AFailedIntegrationCallNamesTheServiceInTheLogTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Two_clients_failing_the_same_job_write_two_different_lines()
    {
        var factory = new SqliteDatabaseConnectionFactory(Options.Create(new StoragePathOptions { DataRoot = _dataRoot }));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, TimeProvider.System),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var log = new CapturingLogger();
        var policy = new IntegrationResiliencePolicy(TimeProvider.System, factory, log);

        foreach (var (id, name) in new[] { ("client-1", "Living room qBittorrent"), ("client-2", "Spare SABnzbd") })
        {
            await policy.ExecuteAsync<string?>(
                new IntegrationResilienceRequest(
                    $"download-client:{id}:telemetry", "telemetry",
                    InitialDelay: TimeSpan.FromMilliseconds(1), MaxDelay: TimeSpan.FromMilliseconds(1),
                    ServiceType: "download-client", ServiceId: id, ServiceName: name),
                _ => throw new HttpRequestException("refused"),
                _ => IntegrationResilienceOutcome.Success,
                CancellationToken.None);
        }

        Assert.Equal(2, log.Warnings.Count);
        Assert.Contains("Living room qBittorrent", log.Warnings[0]);
        Assert.Contains("Spare SABnzbd", log.Warnings[1]);
        Assert.NotEqual(log.Warnings[0], log.Warnings[1]);
    }

    private sealed class CapturingLogger : ILogger<IntegrationResiliencePolicy>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (logLevel == LogLevel.Warning && line.Contains("failed after")) Warnings.Add(line);
        }
    }
}
