using System.Net;
using Deluno.Companions;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Security.Data;
using Deluno.Worker.Companions;
using Moq;

namespace Deluno.Worker.Tests.Companions;

/// <summary>#1055: Weir's connection carries Weir's /ready address, and an older one is given it once.</summary>
public sealed class WeirHealthAddressTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T08:00:00Z");
    private const string Base = "http://127.0.0.1:9347";
    private const string Intake = "/api/v1/intake/webhook/deluno";

    [Fact]
    public async Task The_companion_creates_Weirs_connection_with_its_ready_address_and_it_tests_healthy()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new Handler(request =>
        {
            requests.Add(request);
            // Weir's hand-off route only takes POST; only /ready answers a GET with 200.
            // #1076: a processor may publish its machine name in its capabilities; the connection is named after it.
            if (request.RequestUri!.AbsolutePath == "/api/v1/intake/capabilities")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"machine_name":"MEDIA-BOX"}""") };
            }

            return new HttpResponseMessage(request.RequestUri!.AbsolutePath == "/ready" ? HttpStatusCode.OK : HttpStatusCode.MethodNotAllowed);
        });
        CreateProcessorConnectionRequest? created = null;
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.CreateProcessorConnectionAsync(It.IsAny<CreateProcessorConnectionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateProcessorConnectionRequest, CancellationToken>((request, _) => created = request)
            .ReturnsAsync((CreateProcessorConnectionRequest request, CancellationToken _) => new ProcessorConnectionItem(
                "p1", request.Name!, request.Provider!, request.SubmissionUrl!, request.AuthHeaderName!, request.Secret, true, "unknown", null, null, Now, Now, request.HealthCheckUrl));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));
        var companion = new WeirCompanion(
            new Mock<ICompanionSystem>().Object,
            processors.Object,
            new ProcessorConnectionService(factory.Object),
            new Mock<ISecurityRepository>().Object,
            new Mock<ICompanionDownloader>().Object,
            factory.Object);

        await companion.AddProcessorConnectionAsync(Base, Intake, "X-Webhook-Secret", "s3cret", CancellationToken.None);

        Assert.Equal($"{Base}/ready", created!.HealthCheckUrl);
        Assert.Equal($"{Base}{Intake}", created.SubmissionUrl);
        Assert.Equal("MEDIA-BOX", created.MachineName);
        Assert.Equal("/api/v1/intake/capabilities", requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("s3cret", requests[0].Headers.GetValues("X-Webhook-Secret").Single());
        var request = Assert.Single(requests, item => item.RequestUri!.AbsolutePath == "/ready");
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/ready", request.RequestUri!.AbsolutePath);
        processors.Verify(repo => repo.RecordProcessorConnectionHealthAsync("p1", "healthy", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_companion_says_so_when_Weir_does_not_report_ready()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.CreateProcessorConnectionAsync(It.IsAny<CreateProcessorConnectionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateProcessorConnectionRequest request, CancellationToken _) => new ProcessorConnectionItem(
                "p1", request.Name!, request.Provider!, request.SubmissionUrl!, request.AuthHeaderName!, request.Secret, true, "unknown", null, null, Now, Now, request.HealthCheckUrl));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));
        var companion = new WeirCompanion(
            new Mock<ICompanionSystem>().Object, processors.Object, new ProcessorConnectionService(factory.Object),
            new Mock<ISecurityRepository>().Object, new Mock<ICompanionDownloader>().Object, factory.Object);

        var failure = await Assert.ThrowsAsync<CompanionStepException>(() => companion.AddProcessorConnectionAsync(Base, Intake, null, null, CancellationToken.None));

        Assert.Contains("HTTP 503", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_older_Weir_connection_is_given_the_ready_address_once_and_nothing_else_is_touched()
    {
        var weir = Connection("weir", $"{Base}{Intake}", null);
        var other = Connection("fileflows", "http://192.168.1.5:5000/webhook/deluno", null);
        var lookalike = Connection("other-weir", "http://127.0.0.1:9999/somewhere/else", null);
        var already = Connection("already", $"{Base}{Intake}", $"{Base}/ready");
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([weir, other, lookalike, already]);
        processors.Setup(repo => repo.UpdateProcessorConnectionAsync("weir", It.IsAny<UpdateProcessorConnectionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(weir with { HealthCheckUrl = $"{Base}/ready" });
        var service = new Mock<IProcessorConnectionService>();
        service.Setup(item => item.TestAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorConnectionTestResult("weir", true, "healthy", "The processor answered its health check and reports it is up.", 200, 5));
        var lines = new List<string>();
        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(repo => repo.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((_, message, _, _, _, _, _) => lines.Add(message))
            .ReturnsAsync((Deluno.Jobs.Contracts.ActivityEventItem)null!);

        var updated = await new WeirHealthAddressBackfill(processors.Object, service.Object, activity.Object).RunAsync(CancellationToken.None);

        Assert.Equal(1, updated);
        processors.Verify(repo => repo.UpdateProcessorConnectionAsync(It.Is<string>(id => id != "weir"), It.IsAny<UpdateProcessorConnectionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        processors.Verify(repo => repo.UpdateProcessorConnectionAsync("weir", It.Is<UpdateProcessorConnectionRequest>(request => request.HealthCheckUrl == $"{Base}/ready"), It.IsAny<CancellationToken>()), Times.Once);
        processors.Verify(repo => repo.RecordProcessorConnectionHealthAsync("weir", "healthy", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("health-check address", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_processor_that_is_not_on_this_computer_is_never_touched()
    {
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Connection("weir", $"http://192.168.1.5:9347{Intake}", null)]);

        var updated = await new WeirHealthAddressBackfill(processors.Object, new Mock<IProcessorConnectionService>().Object, new Mock<IActivityFeedRepository>().Object).RunAsync(CancellationToken.None);

        Assert.Equal(0, updated);
        processors.Verify(repo => repo.UpdateProcessorConnectionAsync(It.IsAny<string>(), It.IsAny<UpdateProcessorConnectionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ProcessorConnectionItem Connection(string id, string submissionUrl, string? healthUrl)
        => new(id, id, "generic-webhook", submissionUrl, "X-Webhook-Secret", "s", true, "degraded", null, null, Now, Now, healthUrl);

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"machine_name":"  RIG  "}""", "RIG")]
    [InlineData(HttpStatusCode.OK, """{"other":1}""", null)]
    [InlineData(HttpStatusCode.OK, "not json", null)]
    [InlineData(HttpStatusCode.NotFound, "", null)]
    [InlineData(HttpStatusCode.Unauthorized, """{"machine_name":"RIG"}""", null)]
    public async Task A_processor_publishes_its_machine_name_in_its_capabilities_or_the_address_host_is_used(HttpStatusCode status, string body, string? expected)
    {
        var handler = new Handler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));

        var name = await new ProcessorConnectionService(factory.Object)
            .GetMachineNameAsync($"{Base}{Intake}", "X-Webhook-Secret", "s3cret", CancellationToken.None);

        Assert.Equal(expected, name);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
