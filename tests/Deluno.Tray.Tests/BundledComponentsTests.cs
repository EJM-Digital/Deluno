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

        // Weir's Setup only installs -- it must never be asked to start Weir
        // itself (that hangs forever; see #968). Starting Weir and confirming
        // it is ready are separate, explicit steps.
        Assert.Equal("--silent", weir.SilentArgs);
        Assert.Equal("%LocalAppData%\\Weir\\current\\Weir.exe", weir.PostInstallCommand);
        Assert.Equal("--port 9347 --silent", weir.PostInstallArgs);
        Assert.Equal("http://127.0.0.1:9347/ready", weir.ReadinessUrl);
        Assert.Equal("ready", weir.ReadinessField);
        Assert.Equal("true", weir.ReadinessValue);
        // Weir's supervised start can back off from 2 s to 60 s and retry; it asks callers to allow about three minutes (#1258).
        Assert.Equal(180, weir.ReadinessTimeoutSeconds);

        // Every download client also ends with its own Web UI/API started and
        // polled ready, the same start/poll shape as Weir, so Deluno's own
        // detection can offer it pre-filled (#968).
        foreach (var client in specs.Where(s => s.Category == BundledComponentSpec.DownloadClientCategory))
        {
            Assert.False(string.IsNullOrWhiteSpace(client.PostInstallCommand), $"{client.Kind} has no postInstallCommand.");
            Assert.False(string.IsNullOrWhiteSpace(client.ReadinessUrl), $"{client.Kind} has no readinessUrl.");
        }
    }

    /// <summary>
    /// CI run 39104138771, Windows install leg: Deluge's installer ran 78 s on a slow runner (20 s on the run before) and its
    /// Web UI, a Python program starting deluged beside it, was not answering within the 45 s it was given. The picker ended
    /// "Not installed: Deluge. Deluge started but did not report ready in time", although Deluno started Deluge itself a moment
    /// later. A cold first start gets the three minutes Weir gets.
    /// </summary>
    [Fact]
    public void Deluge_is_given_three_minutes_to_answer_on_its_first_start()
    {
        var deluge = BundledComponents.Load().Single(s => s.Kind == "deluge");
        Assert.Equal(180, deluge.ReadinessTimeoutSeconds);
    }

    [Fact]
    public void Qbittorrent_and_sabnzbd_never_share_a_default_port()
    {
        var specs = BundledComponents.Load();
        var qbittorrent = specs.Single(s => s.Kind == "qbittorrent");
        var sabnzbd = specs.Single(s => s.Kind == "sabnzbd");

        Assert.NotEqual(qbittorrent.DefaultPort, sabnzbd.DefaultPort);
    }

    [Fact]
    public void Qbittorrent_seeds_its_config_at_the_roaming_path_it_actually_reads()
    {
        // qBittorrent (portable=false) reads %AppData%\qBittorrent\qBittorrent.ini
        // (Roaming) -- a clean-VM run showed the Web UI never answering on
        // 8081 because the seed used to write %LocalAppData% instead, a file
        // qbittorrent.exe never looks at.
        var qbittorrent = BundledComponents.Load().Single(s => s.Kind == "qbittorrent");

        Assert.Equal("%AppData%\\qBittorrent\\qBittorrent.ini", qbittorrent.ConfigFilePath);
        Assert.Contains("[LegalNotice]", qbittorrent.ConfigFileContent);
        Assert.Contains("Accepted=true", qbittorrent.ConfigFileContent);
    }

    [Fact]
    public void Transmission_installs_its_vc_plus_plus_prerequisite_before_its_own_msi()
    {
        // Transmission's .msi LaunchConditions refuse with exit code 1603
        // ("The Visual C++ Runtime is not installed or is outdated") on a
        // machine with no recent MSVC-built software already on it, which a
        // clean Windows Server VM is.
        var transmission = BundledComponents.Load().Single(s => s.Kind == "transmission");

        Assert.NotNull(transmission.Prerequisite);
        Assert.Equal("https://aka.ms/vs/17/release/vc_redist.x64.exe", transmission.Prerequisite!.Url);
        Assert.True(transmission.Prerequisite.MustBeSigned);
        Assert.Equal("SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\x64", transmission.Prerequisite.SkipRegistryKeyPath);
        Assert.Equal("Installed", transmission.Prerequisite.SkipRegistryValueName);
    }

    [Fact]
    public void Nzbget_resolves_its_checksum_from_its_own_versioned_signature_file()
    {
        // NZBGet does publish checksums with its release, just not as a
        // fixed-name SHA256SUMS.txt -- the file name itself embeds the
        // version (nzbget-21.1.sig.txt), so it needs a pattern the same way
        // the installer asset itself does.
        var nzbget = BundledComponents.Load().Single(s => s.Kind == "nzbget");

        Assert.False(string.IsNullOrWhiteSpace(nzbget.GithubChecksumAssetPattern));
        Assert.Matches(nzbget.GithubChecksumAssetPattern!, "nzbget-21.1.sig.txt");
    }

    [Fact]
    public void Deluge_resolves_its_latest_windows_build_from_a_directory_listing_not_a_dead_page_url()
    {
        var deluge = BundledComponents.Load().Single(s => s.Kind == "deluge");

        // The old manifest pointed url straight at the download *page*,
        // which downloads as an HTML page, not an installer -- exactly the
        // clean-VM failure this fixes.
        Assert.Null(deluge.Url);
        Assert.False(string.IsNullOrWhiteSpace(deluge.DirectoryListingUrl));
        Assert.False(string.IsNullOrWhiteSpace(deluge.AssetPattern));
    }

    [Fact]
    public void Nzbget_resolves_its_latest_release_by_pattern_not_the_releases_page_url()
    {
        var nzbget = BundledComponents.Load().Single(s => s.Kind == "nzbget");

        // The old manifest's url was the GitHub releases *page*
        // (.../releases/latest), which downloads as HTML far short of
        // minimumBytes -- exactly the clean-VM failure this fixes.
        Assert.Null(nzbget.Url);
        Assert.Equal("nzbgetcom/nzbget", nzbget.GithubRepo);
        Assert.False(string.IsNullOrWhiteSpace(nzbget.GithubAssetPattern));
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
                !string.IsNullOrWhiteSpace(spec.Sha256)
                    || spec.MustBeSigned
                    || !string.IsNullOrWhiteSpace(spec.GithubChecksumAsset)
                    || !string.IsNullOrWhiteSpace(spec.GithubChecksumAssetPattern)
                    || !string.IsNullOrWhiteSpace(spec.ChecksumSuffix),
                $"{spec.Kind} has no pinned checksum, no signature requirement, and no dynamic checksum source.");
        }
    }
}
