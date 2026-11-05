using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Moq;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>#1494: the reader asks a client once for a burst of grabs, never holds a grab back when a client does not answer, and knows no limit for a protocol with none.</summary>
public sealed class DownloadClientLoadReaderTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-05T10:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (DownloadClientLoadReader Reader, Mock<IDownloadClient> Adapter, Clock Clock, DownloadClientItem Item) Rig(Func<Task<DownloadClientLoad?>> answer)
    {
        // A fresh host and port per test: the reader remembers across instances.
        var item = Client("qbittorrent") with { Id = "load-" + Guid.NewGuid().ToString("n"), Port = Random.Shared.Next(20000, 60000) };
        var adapter = new Mock<IDownloadClient>();
        adapter.Setup(client => client.GetLoadAsync(It.IsAny<DownloadClientItem>(), It.IsAny<CancellationToken>())).Returns(answer);
        var registry = new Mock<IDownloadClientRegistry>();
        var client = adapter.Object;
        registry.Setup(r => r.TryGet("qbittorrent", out client)).Returns(true);
        var clock = new Clock();
        return (new DownloadClientLoadReader(registry.Object, clock), adapter, clock, item);
    }

    [Fact]
    public async Task A_burst_of_grabs_asks_the_client_once_and_a_later_grab_asks_again()
    {
        var (reader, adapter, clock, item) = Rig(() => Task.FromResult<DownloadClientLoad?>(new(3, 3)));

        Assert.Equal(new DownloadClientLoad(3, 3), await reader.ReadAsync(item, CancellationToken.None));
        Assert.Equal(new DownloadClientLoad(3, 3), await reader.ReadAsync(item, CancellationToken.None));
        adapter.Verify(client => client.GetLoadAsync(It.IsAny<DownloadClientItem>(), It.IsAny<CancellationToken>()), Times.Once);

        clock.Now += DownloadClientLoadReader.ReuseFor + TimeSpan.FromSeconds(1);
        await reader.ReadAsync(item, CancellationToken.None);
        adapter.Verify(client => client.GetLoadAsync(It.IsAny<DownloadClientItem>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_client_that_does_not_answer_reads_as_unknown_and_is_not_asked_again_at_once()
    {
        var (reader, adapter, clock, item) = Rig(() => throw new HttpRequestException("refused"));

        Assert.Null(await reader.ReadAsync(item, CancellationToken.None));
        Assert.Null(await reader.ReadAsync(item, CancellationToken.None));
        adapter.Verify(client => client.GetLoadAsync(It.IsAny<DownloadClientItem>(), It.IsAny<CancellationToken>()), Times.Once);

        clock.Now += DownloadClientLoadReader.ReuseFailureFor + TimeSpan.FromSeconds(1);
        Assert.Null(await reader.ReadAsync(item, CancellationToken.None));
        adapter.Verify(client => client.GetLoadAsync(It.IsAny<DownloadClientItem>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_protocol_the_registry_does_not_know_reads_as_unknown()
    {
        var registry = new Mock<IDownloadClientRegistry>();
        IDownloadClient? none = null;
        registry.Setup(r => r.TryGet(It.IsAny<string?>(), out none)).Returns(false);

        Assert.Null(await new DownloadClientLoadReader(registry.Object).ReadAsync(Client("qbittorrent") with { Id = "unknown-" + Guid.NewGuid().ToString("n") }, CancellationToken.None));
    }
}
