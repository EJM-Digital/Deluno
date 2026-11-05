using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Libraries.Contracts;
using Moq;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #1494: when the top-priority client is at its own active-download limit, the grab goes to the next eligible client with a
/// free slot, and says why. When every client is full it goes to the top one, as before. Who is eligible is not touched here:
/// the chooser is handed the eligible clients, as the routing rules left them.
/// </summary>
public sealed class AGrabPassesOverAFullClientTests
{
    private sealed class Loads(Dictionary<string, DownloadClientLoad?> byId) : IDownloadClientLoadReader
    {
        public int Reads { get; private set; }

        public Task<DownloadClientLoad?> ReadAsync(DownloadClientItem client, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(byId.GetValueOrDefault(client.Id));
        }
    }

    private static readonly Dictionary<string, (DownloadClientItem Item, int Priority)> Rig = new()
    {
        ["qbit"] = (Client("qbittorrent") with { Id = "qbit", Name = "qBittorrent" }, 10),
        ["trans"] = (Client("transmission") with { Id = "trans", Name = "Transmission" }, 20),
        ["deluge"] = (Client("deluge") with { Id = "deluge", Name = "Deluge" }, 30),
    };

    private static LibraryDownloadClientLinkItem Link(string id)
        => new("link-" + id, "library", id, Rig[id].Item.Name, Rig[id].Priority, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static async Task<(string Chosen, string? Reason)> ChooseAsync(Dictionary<string, DownloadClientLoad?> loads, params string[] ids)
    {
        var pipeline = new AcquisitionDecisionPipeline(new Mock<IMediaSearchPlanner>().Object, loadReader: new Loads(loads));
        var (client, reason) = await pipeline.ChooseDownloadClientAsync(
            ids.Select(Link).ToArray(), false, link => Rig[link.DownloadClientId].Priority, CancellationToken.None, link => Rig[link.DownloadClientId].Item);
        return (client!.DownloadClientId, reason);
    }

    [Fact]
    public async Task A_free_top_client_takes_the_grab_and_nothing_is_said_about_fullness()
    {
        var (chosen, reason) = await ChooseAsync(new() { ["qbit"] = new(2, 3), ["trans"] = new(0, 5) }, "qbit", "trans", "deluge");

        Assert.Equal("qbit", chosen);
        Assert.Equal("qBittorrent has the highest priority (10) of the clients that can take it.", reason);
    }

    [Fact]
    public async Task A_full_top_client_is_passed_over_for_the_next_one_with_a_free_slot_and_the_reason_says_so()
    {
        var (chosen, reason) = await ChooseAsync(new() { ["qbit"] = new(3, 3), ["trans"] = new(1, 5), ["deluge"] = new(0, 3) }, "qbit", "trans", "deluge");

        Assert.Equal("trans", chosen);
        Assert.Equal("qBittorrent was full (3 of 3 running), so this went to Transmission.", reason);
    }

    [Fact]
    public async Task Every_client_in_front_that_is_full_is_named_and_the_first_free_one_by_priority_takes_it()
    {
        var (chosen, reason) = await ChooseAsync(new() { ["qbit"] = new(3, 3), ["trans"] = new(5, 5), ["deluge"] = new(0, 3) }, "qbit", "trans", "deluge");

        Assert.Equal("deluge", chosen);
        Assert.Equal("qBittorrent was full (3 of 3 running) and Transmission was full (5 of 5 running), so this went to Deluge.", reason);
    }

    [Fact]
    public async Task When_every_client_is_full_the_grab_still_goes_to_the_top_client_to_wait_in_its_queue()
    {
        var (chosen, reason) = await ChooseAsync(new() { ["qbit"] = new(3, 3), ["trans"] = new(5, 5), ["deluge"] = new(3, 3) }, "qbit", "trans", "deluge");

        Assert.Equal("qbit", chosen);
        Assert.Equal("qBittorrent has the highest priority (10) of the clients that can take it.", reason);
    }

    [Fact]
    public async Task A_client_with_no_limit_is_never_full()
    {
        var (chosen, _) = await ChooseAsync(new() { ["qbit"] = new(40, null) }, "qbit", "trans");

        Assert.Equal("qbit", chosen);
    }

    [Fact]
    public async Task A_client_whose_load_could_not_be_read_counts_as_free()
    {
        var (chosen, reason) = await ChooseAsync(new() { ["qbit"] = new(3, 3), ["trans"] = null }, "qbit", "trans");

        Assert.Equal("trans", chosen);
        Assert.Equal("qBittorrent was full (3 of 3 running), so this went to Transmission.", reason);

        var (top, _) = await ChooseAsync(new() { ["qbit"] = null, ["trans"] = new(0, 5) }, "qbit", "trans");
        Assert.Equal("qbit", top);
    }

    [Fact]
    public async Task A_client_over_its_limit_counts_as_full()
        => Assert.Equal("trans", (await ChooseAsync(new() { ["qbit"] = new(4, 3) }, "qbit", "trans")).Chosen);

    [Fact]
    public async Task A_single_eligible_client_is_not_asked_at_all()
    {
        var loads = new Loads(new() { ["qbit"] = new(3, 3) });
        var pipeline = new AcquisitionDecisionPipeline(new Mock<IMediaSearchPlanner>().Object, loadReader: loads);

        var (client, reason) = await pipeline.ChooseDownloadClientAsync([Link("qbit")], false, _ => 10, CancellationToken.None, _ => Rig["qbit"].Item);

        Assert.Equal("qbit", client!.DownloadClientId);
        Assert.Equal("qBittorrent is the only client that can take it.", reason);
        Assert.Equal(0, loads.Reads);
    }

    [Fact]
    public async Task Without_a_load_reader_the_choice_is_exactly_what_it_was()
    {
        var pipeline = new AcquisitionDecisionPipeline(new Mock<IMediaSearchPlanner>().Object);

        var (client, _) = await pipeline.ChooseDownloadClientAsync(
            [Link("qbit"), Link("trans")], false, link => Rig[link.DownloadClientId].Priority, CancellationToken.None, link => Rig[link.DownloadClientId].Item);

        Assert.Equal("qbit", client!.DownloadClientId);
    }

    [Fact]
    public void A_load_reads_as_full_only_at_or_over_a_limit()
    {
        Assert.True(new DownloadClientLoad(3, 3).IsFull);
        Assert.False(new DownloadClientLoad(2, 3).IsFull);
        Assert.False(new DownloadClientLoad(99, null).IsFull);
        Assert.Equal("3 of 3 running", new DownloadClientLoad(3, 3).Describe());
    }
}
