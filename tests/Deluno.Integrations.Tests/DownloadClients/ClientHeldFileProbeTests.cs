using System.Runtime.CompilerServices;
using Deluno.Integrations.DownloadClients;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>#1171: a file is held when any client lists it, in its queue, its history or among the items Deluno did not send; and when the clients cannot be asked, it is held.</summary>
public sealed class ClientHeldFileProbeTests
{
    private static T Blank<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static DownloadClientTelemetrySnapshot Snapshot(string? queue = null, string? history = null, string? outside = null)
        => Blank<DownloadClientTelemetrySnapshot>() with
        {
            Queue = queue is null ? [] : [Blank<DownloadQueueItem>() with { SourcePath = queue }],
            History = history is null ? [] : [Blank<DownloadClientHistoryItem>() with { SourcePath = history }],
            OutsideSourcePaths = outside is null ? [] : [outside]
        };

    private static ClientHeldFileProbe Probe(Func<Task<IReadOnlyList<DownloadClientTelemetrySnapshot>>> snapshots)
    {
        var telemetry = new Mock<IDownloadClientTelemetryService>();
        telemetry.Setup(t => t.GetFreshSnapshotsForLeftoverScanAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(snapshots);
        return new ClientHeldFileProbe(telemetry.Object);
    }

    [Theory]
    [InlineData("/dl/Film/Film.mkv", null, null, true)]
    [InlineData("/dl/Film", null, null, true)]
    [InlineData(null, "/dl/Film", null, true)]
    [InlineData(null, null, "/dl/Film/Film.mkv", true)]
    [InlineData("/dl/Other", "/dl/Another", "/dl/Third", false)]
    [InlineData(null, null, null, false)]
    public async Task A_file_is_held_when_the_queue_the_history_or_the_outside_items_claim_it(string? queue, string? history, string? outside, bool held)
    {
        var probe = Probe(() => Task.FromResult<IReadOnlyList<DownloadClientTelemetrySnapshot>>([Snapshot(queue, history, outside)]));

        Assert.Equal(held, await probe.IsHeldByAnyClientAsync("/dl/Film/Film.mkv", CancellationToken.None));
    }

    [Fact]
    public async Task No_clients_means_the_file_is_not_held()
    {
        var probe = Probe(() => Task.FromResult<IReadOnlyList<DownloadClientTelemetrySnapshot>>([]));

        Assert.False(await probe.IsHeldByAnyClientAsync("/dl/Film/Film.mkv", CancellationToken.None));
    }

    [Fact]
    public async Task A_second_client_that_holds_it_counts()
    {
        var probe = Probe(() => Task.FromResult<IReadOnlyList<DownloadClientTelemetrySnapshot>>([Snapshot(), Snapshot(queue: "/dl/Film")]));

        Assert.True(await probe.IsHeldByAnyClientAsync("/dl/Film/Film.mkv", CancellationToken.None));
    }

    [Fact]
    public async Task When_the_clients_cannot_be_asked_the_file_is_taken_as_held_so_nothing_is_cleaned_up_by_mistake()
    {
        var probe = Probe(() => throw new HttpRequestException("down"));

        Assert.True(await probe.IsHeldByAnyClientAsync("/dl/Film/Film.mkv", CancellationToken.None));
    }

    [Fact]
    public async Task A_stop_is_not_taken_as_held()
    {
        var probe = Probe(() => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.IsHeldByAnyClientAsync("/dl/Film/Film.mkv", CancellationToken.None));
    }

    [Fact]
    public async Task The_clients_are_asked_for_a_capture_up_to_twenty_seconds_old()
    {
        TimeSpan? asked = null;
        var telemetry = new Mock<IDownloadClientTelemetryService>();
        telemetry.Setup(t => t.GetFreshSnapshotsForLeftoverScanAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<TimeSpan, CancellationToken>((reuse, _) => asked = reuse)
            .ReturnsAsync([]);

        await new ClientHeldFileProbe(telemetry.Object).IsHeldByAnyClientAsync("/x", CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(20), asked);
    }
}
