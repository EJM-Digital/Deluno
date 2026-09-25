using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// The installer's component picker manifest (#640): the embedded copy of
/// installer/bundled-components.json is what actually ships, so these read it
/// the same way the tray does rather than the source file on disk.
/// </summary>
public sealed class BundledComponentsTests
{
    [Fact]
    public void Loads_the_five_download_clients_and_Weir()
    {
        var specs = BundledComponents.Load();

        Assert.Equal(6, specs.Count);
        var downloadClients = specs.Where(s => s.Category == BundledComponentSpec.DownloadClientCategory).Select(s => s.Kind).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { "deluge", "nzbget", "qbittorrent", "sabnzbd", "transmission" }, downloadClients);

        var weir = specs.Single(s => s.Kind == "weir");
        Assert.Equal(BundledComponentSpec.ProcessorCategory, weir.Category);
        Assert.Equal("AGPL-3.0", weir.Licence);
        Assert.Equal(9347, weir.DefaultPort);
    }

    [Fact]
    public void Every_component_carries_a_licence_and_a_verification_path()
    {
        foreach (var spec in BundledComponents.Load())
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Licence));
            Assert.False(string.IsNullOrWhiteSpace(spec.LicenceUrl));
            // Never ship a component this repo could not verify at all.
            Assert.True(
                !string.IsNullOrWhiteSpace(spec.Sha256) || spec.MustBeSigned || !string.IsNullOrWhiteSpace(spec.GithubChecksumAsset),
                $"{spec.Kind} has no pinned checksum, no signature requirement, and no dynamic checksum source.");
        }
    }
}
