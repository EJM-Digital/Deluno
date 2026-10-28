using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Integrations.Seerr;
using Deluno.Intake.Data;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Connections;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Deluno.Worker.Tests.Connections;

/// <summary>
/// #1301: the scheduled check and "Test all" test a media server too, through its real client, against Jellyfin 12.4.0's own recorded
/// replies (tests/fixtures/recorded/jellyfin), and record the result, the version it reported and the check the Health board shows.
/// </summary>
public sealed class ConnectionHealthTesterMediaServerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T09:00:00Z");

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static MediaServerConnection Server(string id, bool enabled = true, DateTimeOffset? lastTested = null)
        => new(id, MediaServerKinds.Jellyfin, $"Jellyfin {id}", "http://192.168.1.51:8096", "key", enabled, "untested", null, null, lastTested, null, null, Now, Now);

    private sealed class Rig
    {
        public Mock<IMediaServerRepository> Servers { get; } = new();
        public Mock<IConnectionCheckRecorder> Recorder { get; } = new();
        public Mock<IConnectionVersionStore> Versions { get; } = new();
        public ConnectionHealthTester Tester { get; }

        public Rig(HttpMessageHandler jellyfin, params MediaServerConnection[] servers)
        {
            Servers.Setup(item => item.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(servers);
            foreach (var server in servers)
            {
                Servers.Setup(item => item.GetAsync(server.Id, It.IsAny<CancellationToken>())).ReturnsAsync(server);
            }

            Recorder.Setup(item => item.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionCheckItem>());
            Recorder
                .Setup(item => item.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string kind, string id, string name, bool ok, int? _, string message, CancellationToken _) =>
                    new ConnectionCheckedEvent(kind, id, name, ok, false, message, Now.ToString("O")));

            var connections = new Mock<IConnectionsRepository>();
            connections.Setup(item => item.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<IndexerItem>());
            connections.Setup(item => item.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<DownloadClientItem>());
            var seerrs = new Mock<ISeerrRepository>();
            seerrs.Setup(item => item.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SeerrConnection>());
            var processors = new Mock<IProcessorRepository>();
            processors.Setup(item => item.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ProcessorConnectionItem>());
            var intake = new Mock<IIntakeRepository>();
            intake.Setup(item => item.ListIntakeSourcesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Deluno.Intake.Contracts.IntakeSourceItem>());

            var registry = new MediaServerClientRegistry([new JellyfinMediaServerClient(new Factory(jellyfin))]);
            var services = new ServiceCollection().AddSingleton<IMediaServerClientRegistry>(registry).BuildServiceProvider();
            Tester = new ConnectionHealthTester(services, connections.Object, Servers.Object, seerrs.Object, processors.Object, intake.Object, Recorder.Object, Versions.Object);
        }
    }

    private static HttpMessageHandler Healthy() => RecordedReplies.Handler("jellyfin", [RecordedReplies.Jellyfin("identity"), RecordedReplies.Jellyfin("libraries")]);

    [Fact]
    public async Task Test_all_tests_a_media_server_that_is_on_through_its_client_and_records_its_health_version_and_check()
    {
        var rig = new Rig(Healthy(), Server("j1"), Server("off", enabled: false));

        var results = await rig.Tester.TestAllAsync(CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(ConnectionKinds.MediaServer, result.Kind);
        Assert.Equal("j1", result.Id);
        Assert.True(result.Ok, result.Message);
        rig.Servers.Verify(item => item.RecordHealthAsync("j1", It.Is<MediaServerTestResult>(test => test.Ok && test.Version == "12.4.0"), It.IsAny<CancellationToken>()), Times.Once);
        rig.Servers.Verify(item => item.RecordHealthAsync("off", It.IsAny<MediaServerTestResult>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Versions.Verify(item => item.RecordAsync(ConnectionKinds.MediaServer, "j1", It.IsAny<string?>(), "12.4.0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_regular_check_tests_only_a_media_server_nothing_has_used_for_fifteen_minutes_and_a_refusal_is_the_recorded_result()
    {
        var wrongToken = RecordedReplies.Jellyfin("wrong-token").Exchanges[0].Reply;
        var rig = new Rig(
            RecordedReplies.Handler("jellyfin", [], (_, _) => wrongToken.ToResponse()),
            Server("fresh", lastTested: Now.AddMinutes(-5)),
            Server("stale", lastTested: Now.AddMinutes(-30)));

        var results = await rig.Tester.TestStaleAsync(Now - ConnectionCheckRules.RegularInterval, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("stale", result.Id);
        Assert.False(result.Ok);
        Assert.Contains("refused the token", result.Message);
        rig.Servers.Verify(item => item.RecordHealthAsync("fresh", It.IsAny<MediaServerTestResult>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
