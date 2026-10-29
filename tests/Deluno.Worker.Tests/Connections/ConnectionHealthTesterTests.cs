using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.Seerr;
using Deluno.Intake.Data;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Connections;

/// <summary>
/// #1098: the regular check tests only what nothing has used or checked for fifteen minutes; Test all tests
/// everything that is switched on; both record each real result, and the setting turns the regular check off.
/// </summary>
public sealed class ConnectionHealthTesterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T09:00:00Z");

    private static SeerrConnection Seerr(string id, DateTimeOffset? lastTested, bool enabled = true)
        => new(id, $"Seerr {id}", "http://seerr", "key", enabled, "healthy", null, null, lastTested, null, null, Now, Now);

    private sealed class Rig
    {
        public Mock<ISeerrRepository> Seerrs { get; } = new();
        public Mock<ISeerrClient> SeerrClient { get; } = new();
        public Mock<IConnectionCheckRecorder> Recorder { get; } = new();
        public Mock<IConnectionVersionStore> Versions { get; } = new();
        public ConnectionHealthTester Tester { get; }

        public Rig(params SeerrConnection[] servers)
        {
            Seerrs.Setup(item => item.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(servers);
            foreach (var server in servers)
            {
                Seerrs.Setup(item => item.GetAsync(server.Id, It.IsAny<CancellationToken>())).ReturnsAsync(server);
            }

            SeerrClient
                .Setup(item => item.TestAsync(It.IsAny<SeerrConnection>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeerrTestResult(true, "healthy", "Answered.", 120));
            Recorder.Setup(item => item.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionCheckItem>());
            Recorder
                .Setup(item => item.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string kind, string id, string name, bool ok, int? _, string message, CancellationToken _) =>
                    new ConnectionCheckedEvent(kind, id, name, ok, false, message, Now.ToString("O")));

            var connections = new Mock<IConnectionsRepository>();
            connections.Setup(item => item.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<IndexerItem>());
            connections.Setup(item => item.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<DownloadClientItem>());
            var mediaServers = new Mock<IMediaServerRepository>();
            mediaServers.Setup(item => item.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<MediaServerConnection>());
            var processors = new Mock<IProcessorRepository>();
            processors.Setup(item => item.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ProcessorConnectionItem>());
            var intake = new Mock<IIntakeRepository>();
            intake.Setup(item => item.ListIntakeSourcesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Deluno.Intake.Contracts.IntakeSourceItem>());

            var services = new ServiceCollection().AddSingleton(SeerrClient.Object).BuildServiceProvider();
            Tester = new ConnectionHealthTester(services, connections.Object, mediaServers.Object, Seerrs.Object, processors.Object, intake.Object, Recorder.Object, Versions.Object);
        }
    }

    [Fact]
    public async Task The_regular_check_tests_only_a_connection_nothing_has_used_for_fifteen_minutes()
    {
        var rig = new Rig(
            Seerr("fresh", Now.AddMinutes(-5)),
            Seerr("stale", Now.AddMinutes(-30)),
            Seerr("never", null));

        var results = await rig.Tester.TestStaleAsync(Now - ConnectionCheckRules.RegularInterval, CancellationToken.None);

        Assert.Equal(["never", "stale"], results.Select(item => item.Id).Order().ToArray());
        rig.Seerrs.Verify(item => item.RecordHealthAsync("fresh", It.IsAny<SeerrTestResult>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Seerrs.Verify(item => item.RecordHealthAsync("stale", It.IsAny<SeerrTestResult>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_connection_checked_by_a_button_a_minute_ago_is_not_tested_again_by_the_regular_check()
    {
        var rig = new Rig(Seerr("s1", Now.AddHours(-3)));
        rig.Recorder.Setup(item => item.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ConnectionCheckItem(ConnectionKinds.Requests, "s1", "Seerr s1", true, false, "Answered.", Now.AddMinutes(-1))]);

        var results = await rig.Tester.TestStaleAsync(Now - ConnectionCheckRules.RegularInterval, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Test_all_tests_everything_that_is_on_and_records_each_result_but_leaves_a_switched_off_one_alone()
    {
        var rig = new Rig(Seerr("a", Now.AddMinutes(-1)), Seerr("b", Now.AddMinutes(-1)), Seerr("off", null, enabled: false));

        var results = await rig.Tester.TestAllAsync(CancellationToken.None);

        Assert.Equal(["a", "b"], results.Select(item => item.Id).Order().ToArray());
        Assert.All(results, item => Assert.Equal(ConnectionKinds.Requests, item.Kind));
        rig.Recorder.Verify(
            item => item.RecordAsync(ConnectionKinds.Requests, It.IsAny<string>(), It.IsAny<string>(), true, 120, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task A_test_that_throws_is_the_answer_not_a_gap_and_does_not_stop_the_next()
    {
        var rig = new Rig(Seerr("a", null), Seerr("b", null));
        rig.SeerrClient
            .SetupSequence(item => item.TestAsync(It.IsAny<SeerrConnection>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("No route to host"))
            .ReturnsAsync(new SeerrTestResult(true, "healthy", "Answered.", 50));

        var results = await rig.Tester.TestAllAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, item => !item.Ok && item.Message.Contains("No route to host"));
        Assert.Contains(results, item => item.Ok);
    }

    [Fact]
    public async Task One_connection_can_be_tested_by_kind_and_id_and_an_unknown_one_is_not_found()
    {
        var rig = new Rig(Seerr("s1", Now));

        var found = await rig.Tester.TestAsync(ConnectionKinds.Requests, "s1", CancellationToken.None);
        var missing = await rig.Tester.TestAsync(ConnectionKinds.Requests, "nope", CancellationToken.None);

        Assert.NotNull(found);
        Assert.True(found!.Ok);
        Assert.Null(missing);
    }

    [Fact]
    public async Task The_regular_check_always_runs_once_a_pass_with_no_setting_able_to_stop_it()
    {
        var tester = new Mock<IConnectionHealthTester>();
        tester.Setup(item => item.TestStaleAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(item => item.TryClaimScheduledPassAsync(SystemTasks.ConnectionsCheck, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // No settings repository is even registered: nothing the person set can switch the check off (#1432).
        var services = new ServiceCollection().AddSingleton(tester.Object).BuildServiceProvider();
        var service = new RegularConnectionCheckService(
            NullLogger<RegularConnectionCheckService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            jobs.Object,
            new FixedTime(Now));

        await service.RunOnceAsync(CancellationToken.None);
        tester.Verify(item => item.TestStaleAsync(Now - ConnectionCheckRules.RegularInterval, It.IsAny<CancellationToken>()), Times.Once);
        jobs.Verify(item => item.RecordScheduledPassOutcomeAsync(SystemTasks.ConnectionsCheck, It.IsAny<DateTimeOffset>(), "completed", It.IsAny<long>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>#1117: a check that reads a version keeps it with the connection, under the project whose releases it is compared with.</summary>
    [Fact]
    public async Task A_check_keeps_the_version_the_connection_reported()
    {
        var rig = new Rig(Seerr("s1", null));
        rig.SeerrClient
            .Setup(item => item.TestAsync(It.IsAny<SeerrConnection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeerrTestResult(true, "healthy", "Answered.", 120, "Seerr", "3.5.0"));

        await rig.Tester.TestAsync(ConnectionKinds.Requests, "s1", CancellationToken.None);

        rig.Versions.Verify(item => item.RecordAsync(ConnectionKinds.Requests, "s1", "seerr", "3.5.0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_check_that_reads_no_version_stores_nothing()
    {
        var rig = new Rig(Seerr("s1", null));

        await rig.Tester.TestAsync(ConnectionKinds.Requests, "s1", CancellationToken.None);

        rig.Versions.Verify(item => item.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
