using Deluno.Downloader.Torrent.Magnet;

namespace Deluno.Downloader.Tests.Torrent;

public class MagnetIngestorTests
{
    [Fact]
    public void Public_magnet_with_public_tracker_allows_DhtPex()
    {
        var magnet = MagnetUriParser.Parse(
            "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337");
        var decision = MagnetIngestor.Decide(magnet, new MagnetIngestionHint());
        Assert.Equal(MagnetIngestionDecision.DhtPexAllowed, decision);
    }

    [Fact]
    public void Private_suspect_magnet_with_trackers_picks_TrackerOnly()
    {
        // Passkey-shaped path in the tracker URL signals private.
        var magnet = MagnetUriParser.Parse(
            "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&tr=https%3A%2F%2Ftracker.private-site.example%2Fa1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4%2Fannounce");
        var decision = MagnetIngestor.Decide(magnet, new MagnetIngestionHint());
        Assert.Equal(MagnetIngestionDecision.TrackerOnly, decision);
    }

    [Fact]
    public void Private_suspect_magnet_with_no_trackers_refuses_by_default()
    {
        var magnet = MagnetUriParser.Parse(
            "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a");
        // Orchestrator marks this as private-suspect (e.g. category=private).
        var decision = MagnetIngestor.Decide(
            magnet,
            new MagnetIngestionHint(IsPrivateSuspect: true));
        Assert.Equal(MagnetIngestionDecision.RefuseUnlessOverridden, decision);
    }

    [Fact]
    public void Private_suspect_magnet_with_no_trackers_but_user_override_allows_DhtPex()
    {
        var magnet = MagnetUriParser.Parse(
            "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a");
        var decision = MagnetIngestor.Decide(
            magnet,
            new MagnetIngestionHint(IsPrivateSuspect: true, UserAcceptedLeakRisk: true));
        Assert.Equal(MagnetIngestionDecision.DhtPexAllowed, decision);
    }

    [Fact]
    public void Explicit_public_hint_overrides_LooksPrivate_heuristic()
    {
        // Heuristic alone says private (passkey-shaped tracker URL), but
        // the orchestrator explicitly says this destination is public.
        var magnet = MagnetUriParser.Parse(
            "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&tr=https%3A%2F%2Fnot-actually-private.example%2Fa1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4%2Fannounce");
        Assert.True(magnet.LooksPrivate); // sanity: heuristic flags it
        var decision = MagnetIngestor.Decide(
            magnet,
            new MagnetIngestionHint(IsPrivateSuspect: false));
        Assert.Equal(MagnetIngestionDecision.DhtPexAllowed, decision);
    }

    [Fact]
    public void GuardOrThrow_throws_PrivateMagnetLeakException_on_unsafe_path()
    {
        var magnet = MagnetUriParser.Parse(
            "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a");
        var hint = new MagnetIngestionHint(IsPrivateSuspect: true);

        var ex = Assert.Throws<PrivateMagnetLeakException>(
            () => MagnetIngestor.GuardOrThrow(magnet, hint));
        Assert.Equal("c12fe1c06bba254a9dc9f519b335aa7c1367a88a", ex.Infohash);
    }

    [Fact]
    public void GuardOrThrow_does_not_throw_on_safe_paths()
    {
        // Public magnet:
        var publicMagnet = MagnetUriParser.Parse(
            "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&tr=udp%3A%2F%2Fpublic.example%3A6969");
        MagnetIngestor.GuardOrThrow(publicMagnet, new MagnetIngestionHint());

        // Private-suspect with trackers (TrackerOnly path):
        var privateWithTrackers = MagnetUriParser.Parse(
            "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&tr=https%3A%2F%2Ftracker.private.example%2Fa1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4%2Fannounce");
        MagnetIngestor.GuardOrThrow(privateWithTrackers, new MagnetIngestionHint());

        // Private-suspect without trackers but user opted in:
        var privateOverride = MagnetUriParser.Parse(
            "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a");
        MagnetIngestor.GuardOrThrow(privateOverride,
            new MagnetIngestionHint(IsPrivateSuspect: true, UserAcceptedLeakRisk: true));
    }
}
