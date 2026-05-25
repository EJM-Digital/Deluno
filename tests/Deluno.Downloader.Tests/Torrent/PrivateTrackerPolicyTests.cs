using Deluno.Downloader.Torrent.Engine;

namespace Deluno.Downloader.Tests.Torrent;

public class PrivateTrackerPolicyTests
{
    [Fact]
    public void Required_policy_disables_DHT_PEX_LSD()
    {
        // The single most important data assertion in the torrent module.
        // Every other private-tracker requirement (passkey preservation,
        // event=stopped, single-IP, etc.) lands on top of this.
        var policy = PrivateTrackerPolicy.Required;
        Assert.False(policy.DhtEnabled,    "DHT must be off for private torrents — leaking infohash to public DHT is bannable.");
        Assert.False(policy.PexEnabled,    "PEX must be off for private torrents — peer-exchange between connected peers reveals the swarm.");
        Assert.False(policy.LsdEnabled,    "LSD must be off for private torrents — multicast on LAN still reveals the swarm.");
    }

    [Fact]
    public void Required_policy_enforces_all_announce_layer_rules()
    {
        var policy = PrivateTrackerPolicy.Required;
        Assert.True(policy.SendStoppedOnPause,             "Must send event=stopped on pause/shutdown or trackers flag HnR.");
        Assert.True(policy.PreservePasskeyAcrossRedirects, "Tracker URL rewrites (301/307) must keep the passkey intact.");
        Assert.True(policy.BindToSingleOutboundIp,         "Multiple source IPs from one client get accounts banned for cheating.");
        Assert.True(policy.StrictMinInterval,              "Force-reannounce must still honour min interval.");
        Assert.True(policy.ProhibitHttpsDowngrade,         "Never fall back to http:// when tracker is https:// — MitM would harvest the passkey.");
        Assert.True(policy.SendStableKey,                  "key= parameter is mandatory; stable across re-announces.");
        Assert.True(policy.SendCompactAndNoPeerId,         "compact=1 + no_peer_id=1 matches qBit/uTorrent behavior trackers expect.");
        Assert.True(policy.PreferEncryptedConnections,     "MSE/PE encryption preferred (mandatory on some trackers).");
    }
}
