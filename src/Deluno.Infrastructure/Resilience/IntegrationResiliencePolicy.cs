using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
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

    /// <summary>
    /// Closes every circuit recorded for one service — every key built as
    /// "{serviceType}:{serviceId}:...", regardless of what a caller appended
    /// after the id (address, protocol, purpose). Every call site in the
    /// codebase keys its circuits this way, so a prefix match reaches every
    /// circuit a given indexer or download client could have opened, even
    /// when two call sites (a manual Test and a background search, say)
    /// built their own key suffix differently and would otherwise be talking
    /// past each other.
    ///
    /// Clears both the in-memory circuit and the rows persisted in
    /// integration_circuit_states, so a circuit closed this way does not
    /// reappear on the next restart.
    /// </summary>
    Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken);
}

/// <summary>
/// Raised for an HTTP 429 so the real wait the service asked for survives the
/// trip through <see cref="IIntegrationResiliencePolicy"/> instead of being
/// collapsed into a bare status code. A 429 is still a transient failure
/// (<see cref="IntegrationResiliencePolicy.IsTransientHttpStatusCode"/> already
/// says so) but it is not the same shape as a 500 or a timeout: the service
/// said exactly how long to wait, and guessing with the same exponential
/// backoff wastes the budget it was trying to protect.
/// </summary>
public sealed class IntegrationRateLimitedException(string message, TimeSpan? retryAfter)
    : HttpRequestException(message, null, HttpStatusCode.TooManyRequests)
{
    /// <summary>What the response's Retry-After header asked for. Null means it did not send one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed record IntegrationResilienceRequest(
    string Key,
    string Operation,
    int MaxAttempts = 3,
    int FailureThreshold = 3,
    TimeSpan? InitialDelay = null,
    TimeSpan? MaxDelay = null,
    TimeSpan? BreakDuration = null,
    /// <summary>
    /// What kind of thing this is - "indexer", "download-client", "metadata".
    /// </summary>
    string? ServiceType = null,
    /// <summary>The id of the exact service, so a failure can link back to it.</summary>
    string? ServiceId = null,
    /// <summary>
    /// The name the owner gave this service. Without it a failure can only
    /// name the operation, and a person reading "indexer.search failed" is
    /// not told which of their indexers to go and look at.
    /// </summary>
    string? ServiceName = null,
    /// <summary>
    /// A 429 from this service is not tried again in the same call: the service is rested until the Retry-After it sent
    /// (or <see cref="IntegrationResiliencePolicy.DefaultRateLimitPause"/> when it sent none) and skipped meanwhile (#1405).
    /// Off, a 429 is waited out and retried (the metadata services' way).
    /// </summary>
    bool PauseOnRateLimit = false);

public sealed record IntegrationResilienceResult<T>(
    T? Value,
    bool CircuitOpen,
    bool CircuitOpened,
    int Attempts,
    string? FailureMessage,
    DateTimeOffset? RetryAfterUtc,
    IntegrationFailure? Failure = null,
    /// <summary>The service asked to be left alone (HTTP 429) and is rested until <see cref="RetryAfterUtc"/> (#1405).</summary>
    bool RateLimited = false)
{
    /// <summary>
    /// Whether the operation actually produced <see cref="Value"/>.
    ///
    /// Callers used to decide this by null-testing <see cref="Value"/>, which
    /// is correct for a reference type and silently wrong for a value type:
    /// `T?` on an unconstrained generic is plain `T` for a struct, so the test
    /// always passes and the caller reads a `default(T)` as if it were a real
    /// answer. Ask this instead - it means the same thing for both.
    /// </summary>
    public bool Succeeded => FailureMessage is null && !CircuitOpen && !CircuitOpened;
}

public enum IntegrationResilienceOutcome
{
    Success,
    NonRetryableFailure,
    RetryableFailure
}

public sealed class IntegrationResiliencePolicy(
    TimeProvider timeProvider,
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    ILogger<IntegrationResiliencePolicy> logger,
    IConnectivityMonitor? connectivityMonitor = null)
    : IIntegrationResiliencePolicy
{
    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(2);

    /// <summary>How long a service that answered 429 with no Retry-After is left alone (#1405).</summary>
    public static readonly TimeSpan DefaultRateLimitPause = TimeSpan.FromMinutes(1);

    /// <summary>The longest rest a Retry-After can ask for, so a date years away cannot park a service for good.</summary>
    private static readonly TimeSpan MaxRateLimitPause = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a circuit stays open on its 1st, 2nd, 3rd... consecutive
    /// opening, roughly matching Radarr/Sonarr/Prowlarr's indexer/client
    /// disable escalation. A dead integration stops being retried every
    /// minute forever (#732): the wait grows with each opening that follows
    /// directly on the last one, capped at the final entry, and a
    /// successful call resets the ladder back to the first rung.
    /// </summary>
    private static readonly TimeSpan[] EscalatingBreakDurations =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(3)
    ];

    private readonly object _sync = new();
    private readonly Dictionary<string, CircuitState> _circuits = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _stateLoaded;

    public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
        IntegrationResilienceRequest request,
        Func<CancellationToken, Task<T>> operation,
        Func<T, IntegrationResilienceOutcome> classifyResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(classifyResult);

        await EnsureStateLoadedAsync(cancellationToken);
        var normalized = Normalize(request);
        if (TryGetOpenCircuit(normalized.Key, out var retryAfterUtc, out var restedForRateLimit))
        {
            var circuitFailure = restedForRateLimit
                ? IntegrationFailureFactory.FromHttpStatus(
                    normalized.ServiceType ?? "integration",
                    normalized.ServiceId ?? normalized.Key,
                    normalized.ServiceName ?? normalized.Operation,
                    normalized.Operation,
                    HttpStatusCode.TooManyRequests,
                    FormatRateLimitedMessage(retryAfterUtc),
                    retryAfterUtc: retryAfterUtc)
                : IntegrationFailureFactory.CircuitOpen(
                normalized.ServiceType ?? "integration",
                normalized.ServiceId ?? normalized.Key,
                normalized.ServiceName ?? normalized.Operation,
                normalized.Operation,
                retryAfterUtc,
                // Summary is "{ServiceName} {Operation} failed: {Message}",
                // so naming the service here says it twice. It does say when
                // the next try is: a fixed one-minute break re-tried a dead
                // indexer or client every minute forever and told nobody it
                // had stopped trying harder (#732).
                FormatCircuitOpenMessage(retryAfterUtc));
            return new IntegrationResilienceResult<T>(
                Value: default,
                CircuitOpen: true,
                CircuitOpened: false,
                Attempts: 0,
                FailureMessage: circuitFailure.Message,
                RetryAfterUtc: retryAfterUtc,
                Failure: circuitFailure,
                RateLimited: restedForRateLimit);
        }

        Exception? lastException = null;
        T? lastValue = default;
        var attemptsMade = 0;
        var retryableFailure = false;
        TimeSpan? retryAfterOverride;
        DateTimeOffset? rateLimitedUntil = null;
        for (var attempt = 1; attempt <= normalized.MaxAttempts; attempt++)
        {
            attemptsMade = attempt;
            retryAfterOverride = null;
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

                retryableFailure = true;
            }
            catch (Exception exception)
            {
                // A caller cancellation belongs to the caller. A timeout raised
                // by an external service, however, must become the same typed
                // integration failure as every other transport error.
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                lastException = exception;
                retryableFailure = IsTransient(exception);
                if (exception is IntegrationRateLimitedException rateLimited)
                {
                    retryAfterOverride = rateLimited.RetryAfter;
                    if (normalized.PauseOnRateLimit)
                    {
                        // Asked to wait: rest the service for what it said instead of trying again at once (#1405).
                        var pause = rateLimited.RetryAfter is { } asked && asked > TimeSpan.Zero ? asked : DefaultRateLimitPause;
                        rateLimitedUntil = RecordRateLimited(normalized, pause > MaxRateLimitPause ? MaxRateLimitPause : pause);
                        break;
                    }
                }

                if (!retryableFailure)
                {
                    break;
                }
            }

            if (retryableFailure && attempt < normalized.MaxAttempts)
            {
                DelunoObservability.IntegrationRetries.Add(1,
                    new KeyValuePair<string, object?>("operation", normalized.Operation));
                var delay = retryAfterOverride is { } explicitDelay && explicitDelay > TimeSpan.Zero
                    ? explicitDelay
                    : CalculateDelay(normalized, attempt);
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }

        var openedUntil = rateLimitedUntil ?? (retryableFailure ? RecordRetryableFailure(normalized) : null);
        if (openedUntil is not null)
        {
            DelunoObservability.IntegrationCircuitOpened.Add(1,
                new KeyValuePair<string, object?>("operation", normalized.Operation));
        }

        // A failure that cannot say which service it came from is not
        // attributable, and the policy is the one place that knows a failure
        // happened but not what it was talking to. Callers that name
        // themselves get named back; the rest keep the old wording.
        var serviceType = normalized.ServiceType ?? "integration";
        var serviceId = normalized.ServiceId ?? normalized.Key;
        var serviceName = normalized.ServiceName ?? normalized.Operation;

        var failureMessage = lastException?.Message;
        var failure = lastException is null
            ? IntegrationFailureFactory.FromLegacy(
                serviceType,
                serviceId,
                serviceName,
                normalized.Operation,
                "connectivity",
                "The integration operation did not complete successfully after the configured attempts.",
                retryAfterUtc: openedUntil,
                attempts: attemptsMade)
            : IntegrationFailureFactory.FromException(
                serviceType,
                serviceId,
                serviceName,
                normalized.Operation,
                lastException,
                retryScheduled: retryableFailure,
                retryAfterUtc: openedUntil,
                attempts: attemptsMade);
        if (lastException is not null)
        {
            logger.LogWarning(
                lastException,
                "Integration operation {Operation} for {Service} failed after {Attempts} attempts.",
                normalized.Operation.ForLog(),
                serviceName.ForLog(),
                normalized.MaxAttempts);
        }

        return new IntegrationResilienceResult<T>(
            lastValue,
            CircuitOpen: false,
            CircuitOpened: openedUntil is not null,
            Attempts: attemptsMade,
            FailureMessage: failureMessage,
            RetryAfterUtc: openedUntil,
            Failure: failure,
            RateLimited: rateLimitedUntil is not null);
    }

    public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        => TryGetOpenCircuit(NormalizeKey(key), out retryAfterUtc, out _);

    public async Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        // Persisted state may not have been loaded into memory yet on a
        // fresh process (nobody has searched or tested this service since
        // startup); load it first so a stale in-memory circuit never
        // survives this call and reappears later.
        await EnsureStateLoadedAsync(cancellationToken);

        var prefix = $"{serviceType}:{serviceId}:";
        lock (_sync)
        {
            foreach (var key in _circuits.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _circuits.Remove(key);
            }
        }

        try
        {
            await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
                DelunoDatabaseNames.Jobs, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM integration_circuit_states
                WHERE integration_key LIKE @prefix ESCAPE '\'
                """;
            var param = command.CreateParameter();
            param.ParameterName = "@prefix";
            param.Value = EscapeLikePattern(prefix) + "%";
            command.Parameters.Add(param);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to clear persisted circuit breaker state for {ServiceType}:{ServiceId}.",
                serviceType,
                serviceId);
        }
    }

    /// <summary>
    /// "Deluno paused it after repeated failures" on its own told a person
    /// nothing but that a retry loop had stopped; it never said when Deluno
    /// would try again, so a paused indexer or client looked indistinguishable
    /// from one nobody was ever going to look at again (#732).
    /// </summary>
    private static string FormatCircuitOpenMessage(DateTimeOffset retryAfterUtc)
        => $"Deluno paused it after repeated failures; it will try again at {retryAfterUtc.ToLocalTime():HH:mm}.";

    private static string EscapeLikePattern(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public static bool IsTransientHttpStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode is HttpStatusCode.RequestTimeout or
                   HttpStatusCode.TooManyRequests ||
               code >= 500;
    }

    /// <summary>
    /// The wait a 429 response asked for, however it said it: a delta in
    /// seconds or an absolute retry date. Shared by every caller that turns a
    /// 429 into <see cref="IntegrationRateLimitedException"/>, so the header
    /// is parsed once rather than once per integration.
    /// </summary>
    public static TimeSpan? TryGetRetryAfter(HttpResponseMessage response)
        => response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date
                ? date - DateTimeOffset.UtcNow
                : null);

    private static IntegrationResilienceRequest Normalize(IntegrationResilienceRequest request)
        => request with
        {
            Key = NormalizeKey(request.Key),
            Operation = string.IsNullOrWhiteSpace(request.Operation) ? "integration.call" : request.Operation.Trim(),
            MaxAttempts = Math.Clamp(request.MaxAttempts, 1, 20),
            FailureThreshold = Math.Clamp(request.FailureThreshold, 1, 200),
            InitialDelay = request.InitialDelay is { } initial && initial >= TimeSpan.Zero ? initial : DefaultInitialDelay,
            MaxDelay = request.MaxDelay is { } max && max >= TimeSpan.Zero ? max : DefaultMaxDelay,
            // Left null when the caller did not ask for a specific break: an
            // explicit BreakDuration still wins over escalation, but nobody
            // passes one today (#732), so leaving this unset here is what
            // lets RecordRetryableFailure choose the escalating break instead
            // of silently defaulting every circuit to the same one minute.
            BreakDuration = request.BreakDuration is { } duration && duration > TimeSpan.Zero ? duration : null
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
            OperationCanceledException => true,
            IOException => true,
            _ => false
        };

    private bool TryGetOpenCircuit(string key, out DateTimeOffset retryAfterUtc, out bool rateLimited)
    {
        lock (_sync)
        {
            rateLimited = false;
            if (!_circuits.TryGetValue(key, out var state) || state.OpenUntilUtc is null)
            {
                retryAfterUtc = default;
                return false;
            }

            var now = timeProvider.GetUtcNow();
            if (state.OpenUntilUtc > now)
            {
                retryAfterUtc = state.OpenUntilUtc.Value;
                rateLimited = state.RateLimited;
                return true;
            }

            _circuits[key] = state with { OpenUntilUtc = null, RateLimited = false };
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

    /// <summary>"It asked Deluno to wait" says why it is resting and until when, with no failure count in it (#1405).</summary>
    public static string FormatRateLimitedMessage(DateTimeOffset retryAfterUtc)
        => $"It asked Deluno to wait; Deluno will ask again at {retryAfterUtc.UtcDateTime:HH:mm} UTC.";

    /// <summary>Rests a service that said 429 until it can be asked again. The failure ladder does not move: nothing is broken.</summary>
    private DateTimeOffset RecordRateLimited(IntegrationResilienceRequest request, TimeSpan pause)
    {
        var openedUtc = timeProvider.GetUtcNow();
        var openedUntil = openedUtc.Add(pause);
        int escalationLevel;
        lock (_sync)
        {
            _circuits.TryGetValue(request.Key, out var current);
            escalationLevel = current?.EscalationLevel ?? 0;
            _circuits[request.Key] = new CircuitState(0, openedUntil, escalationLevel, RateLimited: true);
        }

        _ = PersistCircuitOpenAsync(request.Key, openedUntil, openedUtc, 0, escalationLevel);
        return openedUntil;
    }

    private DateTimeOffset? RecordRetryableFailure(IntegrationResilienceRequest request)
    {
        DateTimeOffset? openedUntil;
        int failureCount;
        int escalationLevel;
        var openedUtc = timeProvider.GetUtcNow();

        lock (_sync)
        {
            _circuits.TryGetValue(request.Key, out var current);
            failureCount = (current?.FailureCount ?? 0) + 1;
            escalationLevel = current?.EscalationLevel ?? 0;
            openedUntil = null;
            if (failureCount >= request.FailureThreshold)
            {
                // Every opening that follows directly on the last one climbs
                // the ladder one more rung; a call that succeeds calls
                // Reset(), which drops the whole circuit entry (including
                // this level) back to zero.
                //
                // Unless the internet itself is down (#901): a service that
                // cannot be reached only because nothing can is not a service
                // getting worse, and must not be judged as if it were. The
                // circuit still opens - retries still back off - it just does
                // not climb the ladder while the outage lasts, so a service
                // that was healthy before the outage is retried at the same
                // rung once the outage ends rather than one it never earned.
                if (connectivityMonitor?.Current.IsOutage != true)
                {
                    escalationLevel++;
                }

                var breakDuration = request.BreakDuration ?? GetEscalatingBreakDuration(escalationLevel);
                openedUntil = openedUtc.Add(breakDuration);
                failureCount = 0;
            }

            _circuits[request.Key] = new CircuitState(failureCount, openedUntil, escalationLevel);
        }

        if (openedUntil is not null)
        {
            _ = PersistCircuitOpenAsync(request.Key, openedUntil.Value, openedUtc, request.FailureThreshold, escalationLevel);
        }

        return openedUntil;
    }

    /// <summary>
    /// The 1st opening in a row gets the first rung, the 2nd the next rung,
    /// and so on; once <see cref="EscalatingBreakDurations"/> runs out every
    /// further opening in the same streak stays at the last (longest) rung.
    /// </summary>
    private static TimeSpan GetEscalatingBreakDuration(int escalationLevel)
    {
        var index = Math.Clamp(escalationLevel - 1, 0, EscalatingBreakDurations.Length - 1);
        return EscalatingBreakDurations[index];
    }

    private async Task EnsureStateLoadedAsync(CancellationToken cancellationToken)
    {
        if (_stateLoaded) return;

        try
        {
            await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
                DelunoDatabaseNames.Jobs, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT integration_key, circuit_open_until_utc, failure_count, escalation_level
                FROM integration_circuit_states
                WHERE circuit_open_until_utc > @now
                """;
            var param = command.CreateParameter();
            param.ParameterName = "@now";
            param.Value = timeProvider.GetUtcNow().ToString("O");
            command.Parameters.Add(param);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            lock (_sync)
            {
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    var openUntil = DateTimeOffset.Parse(reader.GetString(1));
                    var count = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    var escalationLevel = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                    _circuits[key] = new CircuitState(count, openUntil, escalationLevel);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load persisted circuit breaker states.");
        }
        finally
        {
            _stateLoaded = true;
        }
    }

    private async Task PersistCircuitOpenAsync(
        string key,
        DateTimeOffset openUntilUtc,
        DateTimeOffset openedUtc,
        int failureCount,
        int escalationLevel)
    {
        try
        {
            await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
                DelunoDatabaseNames.Jobs, CancellationToken.None);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO integration_circuit_states
                    (integration_key, circuit_open_until_utc, opened_utc, failure_count, escalation_level)
                VALUES
                    (@key, @openUntil, @openedUtc, @failureCount, @escalationLevel)
                ON CONFLICT(integration_key) DO UPDATE SET
                    circuit_open_until_utc = excluded.circuit_open_until_utc,
                    opened_utc = excluded.opened_utc,
                    failure_count = excluded.failure_count,
                    escalation_level = excluded.escalation_level
                """;

            void AddParam(string name, object value)
            {
                var p = command.CreateParameter();
                p.ParameterName = name;
                p.Value = value;
                command.Parameters.Add(p);
            }

            AddParam("@key", key);
            AddParam("@openUntil", openUntilUtc.ToString("O"));
            AddParam("@openedUtc", openedUtc.ToString("O"));
            AddParam("@failureCount", failureCount);
            AddParam("@escalationLevel", escalationLevel);

            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist circuit breaker state for {Key}.", key.ForLog());
        }
    }

    /// <summary>
    /// <see cref="EscalationLevel"/> is how many consecutive circuit openings
    /// have happened for this key with no success in between — the position
    /// on <see cref="EscalatingBreakDurations"/> the next opening will use.
    /// It rides along with the rest of the persisted circuit row so a
    /// restart resumes the same rung instead of quietly dropping back to a
    /// one-minute break (#732).
    /// </summary>
    private sealed record CircuitState(int FailureCount, DateTimeOffset? OpenUntilUtc, int EscalationLevel = 0, bool RateLimited = false);
}
