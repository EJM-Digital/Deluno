using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Platform;

public sealed class IntegrationFailureContractTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, IntegrationFailureKind.Authentication, IntegrationRetryState.ManualAction)]
    [InlineData(HttpStatusCode.TooManyRequests, IntegrationFailureKind.RateLimit, IntegrationRetryState.RetryScheduled)]
    [InlineData(HttpStatusCode.RequestTimeout, IntegrationFailureKind.Timeout, IntegrationRetryState.RetryScheduled)]
    [InlineData(HttpStatusCode.BadGateway, IntegrationFailureKind.Unavailable, IntegrationRetryState.RetryScheduled)]
    [InlineData(HttpStatusCode.UnprocessableEntity, IntegrationFailureKind.RejectedAction, IntegrationRetryState.ManualAction)]
    public void Http_failures_keep_kind_and_retry_disposition(
        HttpStatusCode statusCode,
        IntegrationFailureKind expectedKind,
        IntegrationRetryState expectedRetryState)
    {
        var failure = IntegrationFailureFactory.FromHttpStatus(
            "download-client",
            "client-1",
            "SABnzbd",
            "grab",
            statusCode,
            upstreamDetail: "{\"error\":\"provider detail\"}");

        Assert.Equal(expectedKind, failure.Kind);
        Assert.Equal(expectedRetryState, failure.RetryState);
        Assert.Equal((int)statusCode, failure.HttpStatus);
        Assert.Equal("{\"error\":\"provider detail\"}", failure.UpstreamDetail);
        Assert.Contains("SABnzbd", failure.Summary, StringComparison.Ordinal);
        Assert.NotEmpty(failure.NextAction);
    }

    [Fact]
    public void Failure_round_trips_with_string_enum_values_and_provider_detail()
    {
        var failure = IntegrationFailureFactory.FromLegacy(
            "indexer",
            "indexer-1",
            "Indexer",
            "search",
            "http-transient",
            "The upstream service returned 503.",
            httpStatus: 503,
            retryAfterUtc: DateTimeOffset.Parse("2026-08-31T12:00:00Z"),
            attempts: 3,
            code: "temporarily-unavailable",
            upstreamDetail: "upstream request id abc",
            externalId: "request-123");

        var json = JsonSerializer.Serialize(failure);
        var restored = JsonSerializer.Deserialize<IntegrationFailure>(json);

        Assert.NotNull(restored);
        Assert.Equal(failure, restored);
        Assert.Contains("\"Kind\":\"Unavailable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"RetryState\":\"RetryScheduled\"", json, StringComparison.Ordinal);
        Assert.Contains("request-123", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_classification_keeps_http_status_and_marks_retryable_transport_errors()
    {
        var exception = new HttpRequestException(
            "Indexer returned a gateway error.",
            null,
            HttpStatusCode.BadGateway);

        var failure = IntegrationFailureFactory.FromException(
            "indexer",
            "indexer-1",
            "Indexer",
            "search",
            exception,
            retryScheduled: true,
            attempts: 2);

        Assert.Equal(IntegrationFailureKind.Unavailable, failure.Kind);
        Assert.Equal(IntegrationRetryState.RetryScheduled, failure.RetryState);
        Assert.Equal(502, failure.HttpStatus);
        Assert.Equal(2, failure.Attempts);
        Assert.True(failure.IsTransient);
    }

    [Theory]
    [InlineData(typeof(SocketException), IntegrationFailureKind.Unavailable)]
    [InlineData(typeof(InvalidDataException), IntegrationFailureKind.MalformedResponse)]
    [InlineData(typeof(UriFormatException), IntegrationFailureKind.Configuration)]
    [InlineData(typeof(UnauthorizedAccessException), IntegrationFailureKind.Configuration)]
    public void Non_http_boundary_exceptions_keep_a_typed_failure_kind(
        Type exceptionType,
        IntegrationFailureKind expectedKind)
    {
        Exception exception;
        if (exceptionType == typeof(SocketException))
        {
            exception = new SocketException((int)SocketError.ConnectionRefused);
        }
        else if (exceptionType == typeof(InvalidDataException))
        {
            exception = new InvalidDataException("The provider payload was invalid.");
        }
        else if (exceptionType == typeof(UriFormatException))
        {
            exception = new UriFormatException("The service URL is invalid.");
        }
        else
        {
            exception = new UnauthorizedAccessException("The configured path is not accessible.");
        }

        var failure = IntegrationFailureFactory.FromException(
            "integration",
            "integration-1",
            "Fixture integration",
            "probe",
            exception);

        Assert.Equal(expectedKind, failure.Kind);
        Assert.NotEmpty(failure.NextAction);
    }
}
