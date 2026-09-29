using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>One background service that ended with an error and is no longer running.</summary>
public sealed record FailedBackgroundService(
    string TypeName,
    string DisplayName,
    string Reason,
    DateTimeOffset FailedUtc);

/// <summary>What Health reads to say a background service has stopped (#1080).</summary>
public interface IBackgroundServiceFailureState
{
    IReadOnlyList<FailedBackgroundService> Failures { get; }
}

/// <summary>
/// The safety net under every <see cref="BackgroundService"/> (#1080).
///
/// <para>In .NET the default is that one background service throwing stops the
/// whole host. Deluno sets
/// <see cref="BackgroundServiceExceptionBehavior.Ignore"/> instead, so the
/// rest of the app keeps running, and this monitor watches each service's
/// <see cref="BackgroundService.ExecuteTask"/>: if one ends with an error it is
/// logged and shown on Health in plain words until Deluno is restarted.</para>
///
/// <para>Every loop still catches its own per-pass failures and carries on;
/// this only catches what one of them missed. A service finishing normally
/// (a feature that is switched off returns straight away) is not a failure.
/// A failure in a hosted service's <c>StartAsync</c> (the database
/// migrations) still stops the host, deliberately: Deluno must not run on a
/// database it could not prepare.</para>
/// </summary>
public sealed class BackgroundServiceFailureMonitor(
    IServiceProvider serviceProvider,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider,
    ILogger<BackgroundServiceFailureMonitor> logger)
    : IHostedLifecycleService, IBackgroundServiceFailureState
{
    private readonly ConcurrentDictionary<string, FailedBackgroundService> _failures = new(StringComparer.Ordinal);

    public IReadOnlyList<FailedBackgroundService> Failures
        => [.. _failures.Values.OrderBy(failure => failure.FailedUtc)];

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs once every hosted service has started, so each one's task exists.</summary>
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        foreach (var service in serviceProvider.GetServices<IHostedService>())
        {
            if (service is BackgroundService background && !ReferenceEquals(background, this))
            {
                _ = WatchAsync(background);
            }
        }

        return Task.CompletedTask;
    }

    internal async Task WatchAsync(BackgroundService service)
    {
        var task = service.ExecuteTask;
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            await RecordAsync(service.GetType(), exception).ConfigureAwait(false);
        }
    }

    private async Task RecordAsync(Type type, Exception exception)
    {
        var failure = new FailedBackgroundService(
            type.Name,
            FriendlyName(type.Name),
            exception.Message,
            timeProvider.GetUtcNow());
        _failures[type.Name] = failure;
        logger.LogError(exception, "Background service {Service} stopped after an error; Deluno keeps running and Health shows it.", type.Name);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var health = scope.ServiceProvider.GetService<IHealthCheckRepository>();
            if (health is not null)
            {
                await health.RequestImmediateRunAsync(HealthCheckCategories.System, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception requestFailure)
        {
            logger.LogDebug(requestFailure, "Could not ask for an immediate health check after {Service} stopped.", type.Name);
        }
    }

    /// <summary>"RankingModelTrainingHostedService" becomes "Ranking model training".</summary>
    internal static string FriendlyName(string typeName)
    {
        var trimmed = typeName;
        foreach (var suffix in new[] { "HostedService", "Service" })
        {
            if (trimmed.Length > suffix.Length && trimmed.EndsWith(suffix, StringComparison.Ordinal))
            {
                trimmed = trimmed[..^suffix.Length];
                break;
            }
        }

        var words = Regex.Replace(trimmed, "(?<=[a-z0-9])(?=[A-Z])", " ").Trim();
        if (words.Length == 0)
        {
            return typeName;
        }

        var builder = new StringBuilder(words.ToLowerInvariant());
        builder[0] = char.ToUpperInvariant(builder[0]);
        return builder.ToString();
    }
}
