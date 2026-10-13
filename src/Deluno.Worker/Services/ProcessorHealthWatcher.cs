using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>Placeholder; the real watcher lands in the next commit (#1261).</summary>
public sealed class ProcessorHealthWatcher(
    ILogger<ProcessorHealthWatcher> logger,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly object[] _unused = [logger, scopeFactory, timeProvider];
    public static readonly TimeSpan DownCadence = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan HealthyCadence = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

    internal Task<Task[]> TickAsync(CancellationToken cancellationToken) => Task.FromResult(Array.Empty<Task>());

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
