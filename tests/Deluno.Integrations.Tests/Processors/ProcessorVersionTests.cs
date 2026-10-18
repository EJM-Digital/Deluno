using System.Net;
using System.Text;
using Deluno.Contracts;
using Deluno.Integrations.Processors;
using Deluno.Tests.Recorded;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Tests.Processors;

/// <summary>#1117: a reachable processor is asked for the optional version it publishes in its capabilities; nothing there means nothing shown.</summary>
public sealed class ProcessorVersionTests
{
    private static ProcessorConnectionItem Connection(string? healthCheckUrl = "http://nas:8788/ready")
        => new(
            "connection-1", "Weir", "generic-webhook", "http://nas:8788/api/v1/intake/webhook/deluno",
            "X-Webhook-Secret", "shared-secret", true, "unknown", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, healthCheckUrl);

    [Fact]
    public async Task A_processor_that_publishes_a_version_says_it()
    {
        var sent = new List<(string Path, string? Secret)>();
        var service = Service(request =>
        {
            sent.Add((request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("X-Webhook-Secret", out var values) ? values.Single() : null));
            return Capabilities();
        });

        var version = await service.ReadVersionAsync(Connection(), CancellationToken.None);

        Assert.Equal("1.0.0-rc.18", version);
        var call = Assert.Single(sent);
        Assert.Equal("/api/v1/intake/capabilities", call.Path);
        Assert.Equal("shared-secret", call.Secret);
    }

    [Fact]
    public async Task When_the_capabilities_answer_cannot_be_had_the_version_comes_from_the_processors_api_description()
    {
        var paths = new List<string>();
        var service = Service(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            // Weir 1.0.0-rc.18's description of its API as recorded (openapi: the version and identity of the document), and Weir's
            // refusal of a secret that is wrong for everything else.
            return request.RequestUri.AbsolutePath == "/openapi.json"
                ? RecordedReplies.Weir("openapi").Response("openapi.json")
                : RecordedReplies.Weir("capabilities-wrong-secret").Response("capabilities");
        });

        var described = System.Text.Json.Nodes.JsonNode.Parse(RecordedReplies.Weir("openapi").Single.Reply.Body!)!["info"]!["version"]!.GetValue<string>();
        Assert.Equal(described, await service.ReadVersionAsync(Connection(), CancellationToken.None));
        Assert.Equal(["/api/v1/intake/capabilities", "/openapi.json"], paths);
    }

    [Fact]
    public async Task The_capabilities_version_wins_and_the_description_is_not_asked()
    {
        var paths = new List<string>();
        var service = Service(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Capabilities();
        });

        Assert.Equal("1.0.0-rc.18", await service.ReadVersionAsync(Connection(), CancellationToken.None));
        Assert.Single(paths);
    }

    [Fact]
    public async Task A_development_build_publishes_its_dev_version()
        => Assert.Equal("0.0.1-dev", await Service(_ => Capabilities(("version", "0.0.1-dev"))).ReadVersionAsync(Connection(), CancellationToken.None));

    [Fact]
    public async Task A_processor_that_publishes_no_version_has_none()
        => Assert.Null(await Service(_ => Capabilities(("version", null))).ReadVersionAsync(Connection(), CancellationToken.None));

    [Fact]
    public async Task A_processor_before_it_had_capabilities_has_none()
        => Assert.Null(await Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).ReadVersionAsync(Connection(), CancellationToken.None));

    [Fact]
    public async Task A_capabilities_answer_that_fails_is_none_not_an_error()
    {
        Assert.Null(await Service(_ => throw new HttpRequestException("reset")).ReadVersionAsync(Connection(), CancellationToken.None));
        Assert.Null(await Service(_ => Json("not json")).ReadVersionAsync(Connection(), CancellationToken.None));
    }

    [Fact]
    public async Task A_credential_that_cannot_be_read_is_not_used_to_ask()
    {
        var sent = 0;
        var service = Service(_ =>
        {
            sent++;
            return Capabilities();
        });

        Assert.Null(await service.ReadVersionAsync(Connection() with { SecretUnreadable = true }, CancellationToken.None));
        Assert.Equal(0, sent);
    }

    [Fact]
    public void Only_the_installer_s_own_processor_is_matched_to_a_release_feed()
    {
        Assert.Equal("weir", ProcessorProjects.ProjectFor(Connection()));
        Assert.Null(ProcessorProjects.ProjectFor(Connection(healthCheckUrl: null)));
        Assert.Null(ProcessorProjects.ProjectFor(Connection(healthCheckUrl: "http://nas:8788/health")));
        Assert.Null(ProcessorProjects.ProjectFor(Connection() with { SubmissionUrl = "https://fileflows.example.test/api/webhook/deluno" }));
    }

    // #1200: after Weir upgraded, Deluno kept the old version until someone tested the connection. Reading the
    // processor's health now stores the version it reports.
    [Fact]
    public async Task Reading_a_reachable_processors_health_stores_the_version_it_reports()
    {
        var store = new Store();
        var service = new ProcessorConnectionService(
            new Factory(new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/capabilities")
                ? Capabilities()
                : new HttpResponseMessage(HttpStatusCode.OK)))),
            versions: store);

        var result = await service.TestAsync(Connection(), CancellationToken.None);

        Assert.True(result.IsReachable);
        var recorded = Assert.Single(store.Recorded);
        Assert.Equal((ConnectionKinds.Processor, "connection-1", "weir", "1.0.0-rc.18"), recorded);
    }

    [Fact]
    public async Task An_unreachable_processor_leaves_the_stored_version_alone()
    {
        var store = new Store();
        var service = new ProcessorConnectionService(
            new Factory(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down")))),
            versions: store);

        var result = await service.TestAsync(Connection(), CancellationToken.None);

        Assert.False(result.IsReachable);
        Assert.Empty(store.Recorded);
    }

    private sealed class Store : IConnectionVersionStore
    {
        public List<(string Kind, string Id, string? Project, string? Version)> Recorded { get; } = [];
        public Task RecordAsync(string kind, string id, string? project, string? version, CancellationToken cancellationToken)
        {
            Recorded.Add((kind, id, project, version));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ConnectionVersionRow>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectionVersionRow>>([]);
        public Task<bool> DismissAsync(string kind, string id, string version, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private static ProcessorConnectionService Service(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(new Factory(new HttpClient(new StubHandler(send))));

    /// <summary>Weir 1.0.0-rc.18's recorded capabilities, with the given values of it changed.</summary>
    private static HttpResponseMessage Capabilities(params (string Path, object? Value)[] edits)
        => Json(RecordedReplies.Weir("capabilities").Body("capabilities", edits));

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
