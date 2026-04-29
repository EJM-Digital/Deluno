using Deluno.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Deluno.Infrastructure.Resilience;

public interface IIntegrationResiliencePolicy
{
    Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
        IntegrationResilienceRequest request,
        Func<CancellationToken, Task<T>> operation,
        Func<T, IntegrationResilienceOutcome> classifyResult,
        CancellationToken cancellationToken);

    bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc);
}

public sealed record IntegrationResilienceRequest(
    string Key,
    string Operation,
    int MaxAttempts = 3,
    int FailureThreshold = 3,
    TimeSpan? InitialDelay = null,
    TimeSpan? MaxDelay = null,
    TimeSpan? BreakDuration = null);

public sealed record IntegrationResilienceResult<T>(
    T? Value,
    bool CircuitOpen,
    bool CircuitOpened,
    int Attempts,
    string? FailureMessage,
    DateTimeOffset? RetryAfterUtc);

public enum IntegrationResilienceOutcome
{
    Success,
    NonRetryableFailure,
    RetryableFailure
}

public sealed class IntegrationResiliencePolicy(
    TimeProvider timeProvider,
    ILogger<IntegrationResiliencePolicy> logger)
    : IIntegrationResiliencePolicy
{
    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultBreakDuration = TimeSpan.FromMinutes(1);
    private readonly object _sync = new();
    private readonly Dictionary<string, CircuitState> _circuits = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
        IntegrationResilienceRequest request,
        Func<CancellationToken, Task<T>> operation,
        Func<T, IntegrationResilienceOutcome> classifyResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(classifyResult);

        var normalized = Normalize(request);
        if (TryGetOpenCircuit(normalized.Key, out var retryAfterUtc))
        {
            return new IntegrationResilienceResult<T>(
                Value: default,
                CircuitOpen: true,
                CircuitOpened: false,
                Attempts: 0,
                FailureMessage: $"{normalized.Operation} is temporarily disabled after repeated failures.",
                RetryAfterUtc: retryAfterUtc);
        }

        Exception? lastException = null;
        T? lastValue = default;
        for (var attempt = 1; attempt <= normalized.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = await operation(cancellationToken);
                lastValue = value;
                var outcome = classifyResult(value);
                if (outcome == IntegrationResilienceOutcome.Success)
                {
                    Reset(normalized.Key);
                    return new IntegrationResilienceResult<T>(value, false, false, attempt, null, null);
                }

                if (outcome == IntegrationResilienceOutcome.NonRetryableFailure)
                {
                    return new IntegrationResilienceResult<T>(value, false, false, attempt, null, null);
                }
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                lastException = exception;
            }

            if (attempt < normalized.MaxAttempts)
            {
                DelunoObservability.IntegrationRetries.Add(1,
                    new KeyValuePair<string, object?>("operation", normalized.Operation));
                await Task.Delay(CalculateDelay(normalized, attempt), timeProvider, cancellationToken);
            }
        }

        var openedUntil = RecordRetryableFailure(normalized);
        if (openedUntil is not null)
        {
            DelunoObservability.IntegrationCircuitOpened.Add(1,
                new KeyValuePair<string, object?>("operation", normalized.Operation));
        }

        var failureMessage = lastException?.Message;
        if (lastException is not null)
        {
            logger.LogWarning(
                lastException,
                "Integration operation {Operation} failed after {Attempts} attempts.",
                normalized.Operation,
                normalized.MaxAttempts);
        }

        return new IntegrationResilienceResult<T>(
            lastValue,
            CircuitOpen: false,
            CircuitOpened: openedUntil is not null,
            Attempts: normalized.MaxAttempts,
            FailureMessage: failureMessage,
            RetryAfterUtc: openedUntil);
    }

    public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        => TryGetOpenCircuit(NormalizeKey(key), out retryAfterUtc);

    public static bool IsTransientHttpStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode is HttpStatusCode.RequestTimeout or
                   HttpStatusCode.TooManyRequests ||
               code >= 500;
    }

    private static IntegrationResilienceRequest Normalize(IntegrationResilienceRequest request)
        => request with
        {
            Key = NormalizeKey(request.Key),
            Operation = string.IsNullOrWhiteSpace(request.Operation) ? "integration.call" : request.Operation.Trim(),
            MaxAttempts = Math.Clamp(request.MaxAttempts, 1, 5),
            FailureThreshold = Math.Clamp(request.FailureThreshold, 1, 20),
            InitialDelay = request.InitialDelay is { } initial && initial >= TimeSpan.Zero ? initial : DefaultInitialDelay,
            MaxDelay = request.MaxDelay is { } max && max >= TimeSpan.Zero ? max : DefaultMaxDelay,
            BreakDuration = request.BreakDuration is { } duration && duration > TimeSpan.Zero ? duration : DefaultBreakDuration
        };

    private static string NormalizeKey(string key)
        => string.IsNullOrWhiteSpace(key) ? "integration:unknown" : key.Trim();

    private static TimeSpan CalculateDelay(IntegrationResilienceRequest request, int failedAttempt)
    {
        var initial = request.InitialDelay ?? DefaultInitialDelay;
        var max = request.MaxDelay ?? DefaultMaxDelay;
        if (initial == TimeSpan.Zero || max == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var factor = Math.Pow(2, Math.Max(0, failedAttempt - 1));
        var delay = TimeSpan.FromMilliseconds(initial.TotalMilliseconds * factor);
        return delay <= max ? delay : max;
    }

    private static bool IsTransient(Exception exception)
        => exception switch
        {
            HttpRequestException httpRequestException
                => httpRequestException.StatusCode is null ||
                   IsTransientHttpStatusCode(httpRequestException.StatusCode.Value),
            TaskCanceledException => true,
            IOException => true,
            _ => false
        };

    private bool TryGetOpenCircuit(string key, out DateTimeOffset retryAfterUtc)
    {
        lock (_sync)
        {
            if (!_circuits.TryGetValue(key, out var state) || state.OpenUntilUtc is null)
            {
                retryAfterUtc = default;
                return false;
            }

            var now = timeProvider.GetUtcNow();
            if (state.OpenUntilUtc > now)
            {
                retryAfterUtc = state.OpenUntilUtc.Value;
                return true;
            }

            _circuits[key] = state with { OpenUntilUtc = null };
            retryAfterUtc = default;
            return false;
        }
    }

    private void Reset(string key)
    {
        lock (_sync)
        {
            _circuits.Remove(key);
        }
    }

    private DateTimeOffset? RecordRetryableFailure(IntegrationResilienceRequest request)
    {
        lock (_sync)
        {
            _circuits.TryGetValue(request.Key, out var current);
            var failureCount = (current?.FailureCount ?? 0) + 1;
            DateTimeOffset? openedUntil = null;
            if (failureCount >= request.FailureThreshold)
            {
                openedUntil = timeProvider.GetUtcNow().Add(request.BreakDuration ?? DefaultBreakDuration);
                failureCount = 0;
            }

            _circuits[request.Key] = new CircuitState(failureCount, openedUntil);
            return openedUntil;
        }
    }

    private sealed record CircuitState(int FailureCount, DateTimeOffset? OpenUntilUtc);
}
