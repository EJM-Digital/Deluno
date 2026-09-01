using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Contracts;

/// <summary>
/// The stable vocabulary for an external integration failure. A failure is
/// deliberately richer than a health colour: it says what happened, which
/// operation was affected, and whether the caller may safely try again.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationFailureKind
{
    Authentication,
    RateLimit,
    Timeout,
    Protocol,
    Unavailable,
    MalformedResponse,
    RejectedAction,
    Configuration,
    CircuitOpen,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationRetryState
{
    NotRetryable,
    Retrying,
    RetryScheduled,
    CircuitOpen,
    ManualAction
}

/// <summary>
/// An attributable, serialisable failure from an indexer, download client,
/// metadata provider, subtitle provider, or another external service.
/// </summary>
public sealed record IntegrationFailure(
    string ServiceType,
    string ServiceId,
    string ServiceName,
    string Operation,
    IntegrationFailureKind Kind,
    IntegrationRetryState RetryState,
    string Message,
    string? Code = null,
    int? HttpStatus = null,
    string? UpstreamDetail = null,
    string? ExternalId = null,
    DateTimeOffset? RetryAfterUtc = null,
    int Attempts = 0)
{
    /// <summary>Whether this failure represents a condition that may clear without a configuration change.</summary>
    public bool IsTransient
        => Kind is IntegrationFailureKind.RateLimit
            or IntegrationFailureKind.Timeout
            or IntegrationFailureKind.Unavailable
            or IntegrationFailureKind.CircuitOpen;

    /// <summary>The old category used by health persistence, retained for backwards-compatible consumers.</summary>
    public string LegacyCategory => Kind switch
    {
        IntegrationFailureKind.Authentication => "auth",
        IntegrationFailureKind.RateLimit => "rateLimit",
        IntegrationFailureKind.Timeout => "timeout",
        IntegrationFailureKind.Protocol => "http",
        IntegrationFailureKind.Unavailable => "connectivity",
        IntegrationFailureKind.MalformedResponse => "unexpected-response",
        IntegrationFailureKind.RejectedAction => "rejected",
        IntegrationFailureKind.Configuration => "configuration",
        IntegrationFailureKind.CircuitOpen => "circuit-open",
        _ => "unknown"
    };

    /// <summary>A short sentence suitable for Health, Activity, and transfer cards.</summary>
    public string Summary
        => $"{ServiceName} {Operation} failed: {Message}";

    /// <summary>What Deluno can safely tell the person to do next.</summary>
    public string NextAction => Kind switch
    {
        IntegrationFailureKind.Authentication => "Check the credential or API key and test the connection again.",
        IntegrationFailureKind.RateLimit => RetryAfterUtc is { } until
            ? $"Wait until {until.ToLocalTime():HH:mm} before trying again."
            : "Wait for the provider's rate limit window before trying again.",
        IntegrationFailureKind.Timeout or IntegrationFailureKind.Unavailable
            when RetryState == IntegrationRetryState.ManualAction
            => "Check the service and network, then replay or try the action again.",
        IntegrationFailureKind.Timeout or IntegrationFailureKind.Unavailable => "Check the service and network. Deluno will retry when the retry window allows.",
        IntegrationFailureKind.Protocol or IntegrationFailureKind.MalformedResponse => "Check the service URL, protocol, and provider version before retrying.",
        IntegrationFailureKind.RejectedAction => "Review the provider response and the requested action before retrying.",
        IntegrationFailureKind.Configuration => "Complete the integration settings, then test the connection again.",
        IntegrationFailureKind.CircuitOpen => RetryAfterUtc is { } circuitUntil
            ? $"Deluno paused this integration until {circuitUntil.ToLocalTime():HH:mm}; test it after that time."
            : "Deluno paused this integration after repeated failures; test it before retrying.",
        _ => "Review the integration details and test the connection again."
    };
}

/// <summary>
/// Central classification for boundary failures. Adapters can retain their
/// protocol-specific detail while every caller receives the same product
/// vocabulary and retry semantics.
/// </summary>
public static class IntegrationFailureFactory
{
    public static IntegrationFailure FromLegacy(
        string serviceType,
        string serviceId,
        string serviceName,
        string operation,
        string? category,
        string message,
        int? httpStatus = null,
        DateTimeOffset? retryAfterUtc = null,
        int attempts = 0,
        string? code = null,
        string? upstreamDetail = null,
        string? externalId = null)
    {
        var normalized = category?.Trim().ToLowerInvariant();
        var kind = normalized switch
        {
            "auth" or "authentication" or "unauthorized" or "forbidden" => IntegrationFailureKind.Authentication,
            "ratelimit" or "rate-limited" or "rate_limit" or "ratelimited" => IntegrationFailureKind.RateLimit,
            "timeout" or "timed-out" => IntegrationFailureKind.Timeout,
            "http-transient" => IntegrationFailureKind.Unavailable,
            "http" or "protocol" => httpStatus is >= 500
                ? IntegrationFailureKind.Unavailable
                : IntegrationFailureKind.Protocol,
            "connectivity" or "unreachable" or "unavailable" => IntegrationFailureKind.Unavailable,
            "unexpected-response" or "malformed-response" or "malformed" => IntegrationFailureKind.MalformedResponse,
            "rejected" or "blocked" or "paused" or "unready" or "planned" or "notfound" or "failed" => IntegrationFailureKind.RejectedAction,
            "configuration" or "invalid_url" => IntegrationFailureKind.Configuration,
            "circuit-open" or "circuit_open" or "circuitopen" => IntegrationFailureKind.CircuitOpen,
            _ => IntegrationFailureKind.Unknown
        };

        return Create(
            serviceType,
            serviceId,
            serviceName,
            operation,
            kind,
            message,
            httpStatus,
            retryAfterUtc,
            attempts,
            code,
            upstreamDetail,
            externalId);
    }

    public static IntegrationFailure FromHttpStatus(
        string serviceType,
        string serviceId,
        string serviceName,
        string operation,
        HttpStatusCode statusCode,
        string? message = null,
        string? upstreamDetail = null,
        DateTimeOffset? retryAfterUtc = null,
        int attempts = 0)
    {
        var status = (int)statusCode;
        var kind = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? IntegrationFailureKind.Authentication
            : statusCode is HttpStatusCode.RequestTimeout
                ? IntegrationFailureKind.Timeout
                : statusCode is HttpStatusCode.TooManyRequests
                    ? IntegrationFailureKind.RateLimit
                    : status >= 500
                        ? IntegrationFailureKind.Unavailable
                        : status >= 400
                            ? IntegrationFailureKind.RejectedAction
                            : IntegrationFailureKind.Protocol;

        return Create(
            serviceType,
            serviceId,
            serviceName,
            operation,
            kind,
            message ?? $"The service returned HTTP {status}.",
            status,
            retryAfterUtc,
            attempts,
            code: statusCode.ToString(),
            upstreamDetail: upstreamDetail,
            retryScheduled: kind is IntegrationFailureKind.RateLimit or IntegrationFailureKind.Timeout or IntegrationFailureKind.Unavailable);
    }

    public static IntegrationFailure FromException(
        string serviceType,
        string serviceId,
        string serviceName,
        string operation,
        Exception exception,
        bool retryScheduled = false,
        DateTimeOffset? retryAfterUtc = null,
        int attempts = 0,
        string? externalId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is HttpRequestException http && http.StatusCode is { } statusCode)
        {
            var failure = FromHttpStatus(
                serviceType,
                serviceId,
                serviceName,
                operation,
                statusCode,
                http.Message,
                retryAfterUtc: retryAfterUtc,
                attempts: attempts);
            if (retryScheduled && failure.RetryState == IntegrationRetryState.NotRetryable)
            {
                return failure with
                {
                    RetryState = IntegrationRetryState.RetryScheduled,
                    ExternalId = externalId
                };
            }

            return externalId is null ? failure : failure with { ExternalId = externalId };
        }

        var kind = exception switch
        {
            OperationCanceledException => IntegrationFailureKind.Timeout,
            HttpRequestException => IntegrationFailureKind.Unavailable,
            SocketException => IntegrationFailureKind.Unavailable,
            IOException => IntegrationFailureKind.Unavailable,
            UriFormatException or ArgumentException => IntegrationFailureKind.Configuration,
            JsonException or NotSupportedException or InvalidDataException or FormatException => IntegrationFailureKind.MalformedResponse,
            UnauthorizedAccessException => IntegrationFailureKind.Configuration,
            InvalidOperationException => IntegrationFailureKind.Protocol,
            _ => IntegrationFailureKind.Unknown
        };

        return Create(
            serviceType,
            serviceId,
            serviceName,
            operation,
            kind,
            exception.Message,
            retryAfterUtc: retryAfterUtc,
            attempts: attempts,
            externalId: externalId,
            retryScheduled: retryScheduled);
    }

    public static IntegrationFailure CircuitOpen(
        string serviceType,
        string serviceId,
        string serviceName,
        string operation,
        DateTimeOffset? retryAfterUtc,
        string? message = null)
        => Create(
            serviceType,
            serviceId,
            serviceName,
            operation,
            IntegrationFailureKind.CircuitOpen,
            message ?? "Deluno paused this integration after repeated failures.",
            retryAfterUtc: retryAfterUtc);

    private static IntegrationFailure Create(
        string serviceType,
        string serviceId,
        string serviceName,
        string operation,
        IntegrationFailureKind kind,
        string message,
        int? httpStatus = null,
        DateTimeOffset? retryAfterUtc = null,
        int attempts = 0,
        string? code = null,
        string? upstreamDetail = null,
        string? externalId = null,
        bool retryScheduled = false)
    {
        var retryState = kind switch
        {
            IntegrationFailureKind.CircuitOpen => IntegrationRetryState.CircuitOpen,
            IntegrationFailureKind.Authentication or
                IntegrationFailureKind.Protocol or
                IntegrationFailureKind.MalformedResponse or
                IntegrationFailureKind.RejectedAction or
                IntegrationFailureKind.Configuration => IntegrationRetryState.ManualAction,
            _ when retryScheduled => IntegrationRetryState.RetryScheduled,
            _ when retryAfterUtc is not null => IntegrationRetryState.RetryScheduled,
            IntegrationFailureKind.RateLimit or
                IntegrationFailureKind.Timeout or
                IntegrationFailureKind.Unavailable => IntegrationRetryState.RetryScheduled,
            _ => IntegrationRetryState.NotRetryable
        };

        return new IntegrationFailure(
            ServiceType: string.IsNullOrWhiteSpace(serviceType) ? "integration" : serviceType.Trim(),
            ServiceId: string.IsNullOrWhiteSpace(serviceId) ? "unknown" : serviceId.Trim(),
            ServiceName: string.IsNullOrWhiteSpace(serviceName) ? "External service" : serviceName.Trim(),
            Operation: string.IsNullOrWhiteSpace(operation) ? "request" : operation.Trim(),
            Kind: kind,
            RetryState: retryState,
            Message: string.IsNullOrWhiteSpace(message) ? "The integration request failed." : message.Trim(),
            Code: string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            HttpStatus: httpStatus,
            UpstreamDetail: string.IsNullOrWhiteSpace(upstreamDetail) ? null : upstreamDetail.Trim(),
            ExternalId: string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim(),
            RetryAfterUtc: retryAfterUtc,
            Attempts: Math.Max(0, attempts));
    }
}
