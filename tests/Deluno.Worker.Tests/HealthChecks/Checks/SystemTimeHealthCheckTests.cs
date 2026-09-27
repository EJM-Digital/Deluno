using System.Net;
using Deluno.Contracts;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// The machine's own clock, checked against a trusted time source's response
/// headers (#868). A wrong clock breaks private-tracker announces and
/// TLS/HTTPS handshakes in ways that do not look like a clock problem, so
/// this is checked on the same schedule as every other health check rather
/// than left to be noticed by hand.
/// </summary>
public sealed class SystemTimeHealthCheckTests
{
    [Fact]
    public async Task A_clock_within_tolerance_of_the_time_source_passes()
    {
        var sourceUtc = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var localTime = new StubTimeProvider(sourceUtc.AddMinutes(1));
        var check = new SystemTimeHealthCheck(HttpClientFactoryReturning(sourceUtc), localTime);

        var outcomes = await check.RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal(HealthCheckCategories.System, check.Category);
    }

    [Fact]
    public async Task A_clock_far_ahead_of_the_time_source_is_flagged()
    {
        var sourceUtc = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var localTime = new StubTimeProvider(sourceUtc.AddMinutes(20));
        var check = new SystemTimeHealthCheck(HttpClientFactoryReturning(sourceUtc), localTime);

        var outcomes = await check.RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Contains("ahead of", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("private tracker", outcome.Cause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_trusted_time_source_that_cannot_be_reached_is_unknown_not_a_failure()
    {
        var localTime = new StubTimeProvider(DateTimeOffset.UtcNow);
        var check = new SystemTimeHealthCheck(HttpClientFactoryThatThrows(), localTime);

        var outcomes = await check.RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
    }

    private static IHttpClientFactory HttpClientFactoryReturning(DateTimeOffset sourceUtc)
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Date = sourceUtc;
            return response;
        });
        return FactoryFor(new HttpClient(handler));
    }

    private static IHttpClientFactory HttpClientFactoryThatThrows()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("No route to host."));
        return FactoryFor(new HttpClient(handler));
    }

    private static IHttpClientFactory FactoryFor(HttpClient client)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(instance => instance.CreateClient(It.IsAny<string>())).Returns(client);
        return factory.Object;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
