using System.Collections.Concurrent;
using Deluno.Contracts;

namespace Deluno.Integrations.Metadata;

/// <summary>
/// What Deluno knows, process-wide, about whether its metadata service is
/// answering: the last failure it saw, and any pause the service itself asked
/// for (a 429's Retry-After).
///
/// A singleton on purpose. <see cref="TmdbMetadataProvider"/> is a typed
/// HttpClient, so a new one is built for nearly every request; a failure kept
/// on the provider was gone before /api/metadata/status could report it, and
/// the Check now chip said "Working" from the configuration alone (#1090).
/// </summary>
public sealed class MetadataServiceHealth(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pausedUntil = new(StringComparer.OrdinalIgnoreCase);
    private IntegrationFailure? _lastFailure;

    /// <summary>The longest one gateway call may take. Ten seconds is long for a lookup and short enough that a dead gateway cannot hold a screen or a job lane.</summary>
    /// <summary>The circuit families metadata calls use ("metadata:broker:...", "metadata:tmdb:...").</summary>
    public static readonly string[] Sources = ["broker", "tmdb", "omdb", "tvmaze"];

    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The longest a call will sit out a pause the service asked for. Longer than this and the call is deferred instead.</summary>
    public TimeSpan MaxPauseWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The longest pause Deluno will honour from a header, so a bad value cannot silence metadata for a day.</summary>
    public TimeSpan MaxPause { get; init; } = TimeSpan.FromMinutes(5);

    public IntegrationFailure? LastFailure => Volatile.Read(ref _lastFailure);

    public void RecordFailure(IntegrationFailure failure) => Interlocked.Exchange(ref _lastFailure, failure);

    public void RecordSuccess() => Interlocked.Exchange(ref _lastFailure, null);

    /// <summary>Forgets the last failure and every pause: Check now and saving the metadata settings start from a clean slate.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _lastFailure, null);
        _pausedUntil.Clear();
    }

    public DateTimeOffset PauseFor(string host, TimeSpan duration)
    {
        var until = _time.GetUtcNow().Add(duration > MaxPause ? MaxPause : duration);
        _pausedUntil.AddOrUpdate(host, until, (_, existing) => existing > until ? existing : until);
        return until;
    }

    /// <summary>How much longer <paramref name="host"/> has asked not to be called; zero when it has not.</summary>
    public TimeSpan PausedFor(string host)
    {
        if (!_pausedUntil.TryGetValue(host, out var until))
        {
            return TimeSpan.Zero;
        }

        var remaining = until - _time.GetUtcNow();
        if (remaining > TimeSpan.Zero)
        {
            return remaining;
        }

        _pausedUntil.TryRemove(host, out _);
        return TimeSpan.Zero;
    }
}

/// <summary>
/// Watches one piece of work (a search, a refresh) for the failure that made
/// it come back empty. Empty is a valid answer; "the service did not answer"
/// is not, and the two must never look alike (#1090).
/// </summary>
public interface IMetadataFailureScope : IDisposable
{
    /// <summary>The last failure any metadata call inside this scope hit, or null if they all went through.</summary>
    IntegrationFailure? Failure { get; }
}

public sealed class NoMetadataFailureScope : IMetadataFailureScope
{
    public static NoMetadataFailureScope Instance { get; } = new();
    public IntegrationFailure? Failure => null;
    public void Dispose() { }
}

/// <summary>The plain words for a metadata failure, in one place so the Add screen, the chip and Health say the same thing.</summary>
public static class MetadataFailureWords
{
    public static string Describe(IntegrationFailure failure)
        => failure.Kind switch
        {
            IntegrationFailureKind.RateLimit or IntegrationFailureKind.QuotaExceeded
                => "The metadata service is busy; try again in a minute.",
            IntegrationFailureKind.Authentication or IntegrationFailureKind.Configuration
                => "The metadata service did not accept Deluno's connection; check the metadata settings.",
            _ => "The metadata service isn't answering; try again in a minute."
        };
}

/// <summary>Clears what Deluno remembers about the metadata service failing: the last failure, any pause, and every metadata circuit.</summary>
public sealed class MetadataServiceReset(
    MetadataServiceHealth health,
    Deluno.Infrastructure.Resilience.IIntegrationResiliencePolicy resiliencePolicy)
    : Deluno.Infrastructure.Resilience.IMetadataServiceReset
{
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        health.Reset();
        foreach (var source in MetadataServiceHealth.Sources)
        {
            await resiliencePolicy.CloseCircuitsAsync("metadata", source, cancellationToken);
        }
    }
}
