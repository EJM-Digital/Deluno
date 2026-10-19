using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Deluno.Companions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// #1118: a newer version of a connected app is a notice you can act on. What shows (shown, dismissed, shown again
/// for a newer version, cleared by a check), what Deluno may update itself, and the Update now flow with the
/// installer, the processes and HTTP replaced.
/// </summary>
public sealed class ConnectionUpdateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-update-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ the notice

    [Theory]
    [InlineData("4.6.7", "5.0.2", null, true)]
    [InlineData("4.6.7", "5.0.2", "5.0.2", false)]
    [InlineData("4.6.7", "5.0.3", "5.0.2", true)]
    [InlineData("5.0.2", "5.0.2", null, false)]
    [InlineData("5.0.3", "5.0.2", null, false)]
    [InlineData("5.1.0-beta1", "5.0.2", null, false)]
    public void A_notice_is_shown_for_a_newer_stable_version_until_it_is_dismissed_and_returns_only_for_a_newer_one(string installed, string latest, string? dismissed, bool shown)
        => Assert.Equal(shown, ConnectionUpdateDecision.NoticeShown(installed, latest, dismissed));

    [Fact]
    public async Task The_list_shows_the_notice_then_hides_it_when_dismissed_brings_it_back_for_a_newer_version_and_drops_it_once_a_check_sees_the_new_version()
    {
        var rows = new List<ConnectionVersionRow> { Row("4.6.7", dismissed: null) };
        var latest = "5.0.2";
        var service = Service(rows, () => new LatestRelease(latest, $"https://example.test/{latest}"));

        var shown = Assert.Single(await service.ListAsync(CancellationToken.None));
        Assert.True(shown.Notice);
        Assert.Equal("qBittorrent", shown.AppName);
        Assert.Equal("https://example.test/5.0.2", shown.ReleaseUrl);

        rows[0] = Row("4.6.7", dismissed: "5.0.2");
        var dismissed = Assert.Single(await service.ListAsync(CancellationToken.None));
        Assert.False(dismissed.Notice);

        latest = "5.0.3";
        Assert.True(Assert.Single(await service.ListAsync(CancellationToken.None)).Notice);

        // A check reads the new version: nothing is out of date, so there is nothing to show.
        rows[0] = Row("5.0.3", dismissed: "5.0.2");
        Assert.Empty(await service.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Dismissing_stores_the_newest_version_known_for_that_connection()
    {
        var rows = new List<ConnectionVersionRow> { Row("4.6.7", dismissed: null) };
        var store = StoreOver(rows);
        var service = Service(rows, () => new LatestRelease("5.0.2", null), store);

        Assert.Equal("5.0.2", await service.DismissAsync(ConnectionKinds.DownloadClient, "qb1", CancellationToken.None));
        store.Verify(item => item.DismissAsync(ConnectionKinds.DownloadClient, "qb1", "5.0.2", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(await service.DismissAsync(ConnectionKinds.DownloadClient, "missing", CancellationToken.None));
    }

    // ------------------------------------------------------------------ what Deluno may update

    [Fact]
    public void Weir_installed_by_Deluno_on_this_computer_updates_to_its_latest_release()
    {
        var option = ConnectionUpdateDecision.Decide(Facts("weir", "3.2.0", "3.3.0", host: "127.0.0.1", pinned: null, installedByDeluno: true));

        Assert.Equal(ConnectionUpdateModes.UpdateNow, option.Mode);
        Assert.Equal("3.3.0", option.TargetVersion);
    }

    [Fact]
    public void A_pinned_build_newer_than_the_installed_one_is_installed_and_the_message_says_when_the_latest_is_not_checked_yet()
    {
        var option = ConnectionUpdateDecision.Decide(Facts("qbittorrent", "4.6.7", "5.3.0", host: "localhost", pinned: "5.2.1", installedByDeluno: true));

        Assert.Equal(ConnectionUpdateModes.UpdateNow, option.Mode);
        Assert.Equal("5.2.1", option.TargetVersion);
        Assert.Contains("hasn't checked 5.3.0 yet", option.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pinned_build_that_is_not_newer_than_the_installed_one_is_never_installed()
    {
        var option = ConnectionUpdateDecision.Decide(Facts("sabnzbd", "5.0.4", "5.1.0", host: "127.0.0.1", pinned: "5.0.4", installedByDeluno: true));

        Assert.Equal(ConnectionUpdateModes.NotChecked, option.Mode);
        Assert.Null(option.TargetVersion);
        Assert.Equal("Deluno hasn't checked 5.1.0 yet, so it won't update SABnzbd for you. To update now, update it in SABnzbd itself. Deluno offers Update now here once a Deluno release has checked 5.1.0; the release notes say what changed.", option.Message);
    }

    [Theory]
    [InlineData("192.168.1.51")]
    [InlineData("nas.example.com")]
    public void An_app_on_another_computer_gets_release_notes_and_a_how_to_never_Update_now(string host)
    {
        var option = ConnectionUpdateDecision.Decide(Facts("qbittorrent", "4.6.7", "5.0.2", host, "5.2.1", installedByDeluno: true));

        Assert.Equal(ConnectionUpdateModes.Manual, option.Mode);
        Assert.Equal(ConnectionUpdateDecision.ElsewhereHowTo, option.Message);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("gluetun")]
    [InlineData("host.docker.internal")]
    [InlineData("172.18.0.4")]
    public void A_host_that_looks_like_a_container_says_to_pull_the_new_image(string host)
    {
        var option = ConnectionUpdateDecision.Decide(Facts("qbittorrent", "4.6.7", "5.0.2", host, "5.2.1", installedByDeluno: false));

        Assert.Equal(ConnectionUpdateModes.Manual, option.Mode);
        Assert.Equal("Pull the new image and restart the container.", option.Message);
    }

    [Fact]
    public void An_app_on_this_computer_that_Deluno_did_not_install_is_not_updated_by_Deluno()
    {
        var option = ConnectionUpdateDecision.Decide(Facts("qbittorrent", "4.6.7", "5.0.2", "127.0.0.1", "5.2.1", installedByDeluno: false));

        Assert.Equal(ConnectionUpdateModes.Manual, option.Mode);
        Assert.Equal(ConnectionUpdateDecision.ElsewhereHowTo, option.Message);
    }

    [Fact]
    public void Nothing_is_updated_where_this_Deluno_cannot_run_installers()
    {
        var facts = Facts("weir", "3.2.0", "3.3.0", "127.0.0.1", null, installedByDeluno: true) with { CanInstallCompanions = false };

        Assert.Equal(ConnectionUpdateModes.Manual, ConnectionUpdateDecision.Decide(facts).Mode);
    }

    [Fact]
    public void The_pinned_versions_are_the_ones_in_the_pinned_installers()
    {
        Assert.Contains(CompanionCatalog.QbittorrentVersion, CompanionCatalog.Qbittorrent.FileName, StringComparison.Ordinal);
        Assert.Contains(CompanionCatalog.SabnzbdVersion, CompanionCatalog.Sabnzbd.FileName, StringComparison.Ordinal);
        Assert.Contains(CompanionCatalog.QbittorrentVersion, CompanionCatalog.Qbittorrent.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(CompanionCatalog.SabnzbdVersion, CompanionCatalog.Sabnzbd.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the Update now flow

    [Fact]
    public async Task Update_now_runs_the_tools_update_checks_the_connection_straight_away_and_says_Updated()
    {
        var rows = new List<ConnectionVersionRow> { Row("4.6.7") };
        var tool = new Mock<IUpdatableCompanion>();
        SetUpQbittorrent(tool);
        var tester = new Mock<IConnectionHealthTester>();
        tester.Setup(item => item.TestAsync(ConnectionKinds.DownloadClient, "qb1", It.IsAny<CancellationToken>()))
            .Callback(() => rows[0] = Row("5.2.1"))
            .ReturnsAsync((ConnectionCheckedEvent?)null);
        var activity = new Mock<IActivityFeedRepository>();
        var service = Service(rows, () => new LatestRelease("5.3.0", null), tools: [tool.Object], tester: tester.Object, activity: activity.Object);

        var result = await service.UpdateNowAsync(ConnectionKinds.DownloadClient, "qb1", "http://127.0.0.1:7879", CancellationToken.None);

        Assert.Equal("updated", result.Outcome);
        Assert.Equal("Updated to 5.2.1.", result.Message);
        tool.Verify(item => item.UpdateAsync(It.IsAny<CompanionRunContext>(), It.IsAny<CancellationToken>()), Times.Once);
        activity.Verify(item => item.RecordActivityAsync("connection.updated", It.Is<string>(text => text.Contains("5.2.1", StringComparison.Ordinal)), null, null, ConnectionKinds.DownloadClient, "qb1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_now_says_Nothing_changed_when_the_app_still_reports_the_old_version()
    {
        var rows = new List<ConnectionVersionRow> { Row("4.6.7") };
        var tool = new Mock<IUpdatableCompanion>();
        SetUpQbittorrent(tool);
        var service = Service(rows, () => new LatestRelease("5.3.0", null), tools: [tool.Object]);

        var result = await service.UpdateNowAsync(ConnectionKinds.DownloadClient, "qb1", "http://127.0.0.1:7879", CancellationToken.None);

        Assert.Equal("unchanged", result.Outcome);
        Assert.StartsWith("Nothing changed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_now_reports_a_failure_in_plain_words_and_never_claims_an_update()
    {
        var rows = new List<ConnectionVersionRow> { Row("4.6.7") };
        var tool = new Mock<IUpdatableCompanion>();
        SetUpQbittorrent(tool);
        tool.Setup(item => item.UpdateAsync(It.IsAny<CompanionRunContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CompanionDownloadException("qbittorrent_5.2.1_x64_setup.exe is not the build Deluno expects (its SHA-256 does not match). Nothing was installed."));
        var tester = new Mock<IConnectionHealthTester>();
        var service = Service(rows, () => new LatestRelease("5.3.0", null), tools: [tool.Object], tester: tester.Object);

        var result = await service.UpdateNowAsync(ConnectionKinds.DownloadClient, "qb1", "http://127.0.0.1:7879", CancellationToken.None);

        Assert.Equal("failed", result.Outcome);
        Assert.StartsWith("Failed: ", result.Message, StringComparison.Ordinal);
        Assert.Contains("SHA-256 does not match", result.Message, StringComparison.Ordinal);
        tester.Verify(item => item.TestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_now_does_nothing_for_an_app_Deluno_may_not_update()
    {
        var rows = new List<ConnectionVersionRow> { Row("4.6.7") };
        var tool = new Mock<IUpdatableCompanion>();
        SetUpQbittorrent(tool);
        tool.Setup(item => item.IsInstalled()).Returns(false);
        var service = Service(rows, () => new LatestRelease("5.3.0", null), tools: [tool.Object]);

        var result = await service.UpdateNowAsync(ConnectionKinds.DownloadClient, "qb1", "http://127.0.0.1:7879", CancellationToken.None);

        Assert.Equal("failed", result.Outcome);
        tool.Verify(item => item.UpdateAsync(It.IsAny<CompanionRunContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -------------------------------------------------- the real installers, with the system and HTTP replaced

    [Fact]
    public async Task qBittorrent_is_closed_installed_through_the_elevated_checked_path_and_started_again()
    {
        var system = new Mock<ICompanionSystem>();
        var elevatedArguments = string.Empty;
        system.SetupGet(item => item.ProgramFiles).Returns(@"C:\Program Files");
        system.SetupGet(item => item.RoamingAppData).Returns(@"C:\Users\test\AppData\Roaming");
        system.Setup(item => item.FileExists(It.IsAny<string>())).Returns(true);
        system.Setup(item => item.RunElevatedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, arguments, _) => elevatedArguments = arguments)
            .ReturnsAsync(new CompanionRunResult(0, false));
        var downloader = new Mock<ICompanionDownloader>();
        downloader.Setup(item => item.DownloadAsync(CompanionCatalog.Qbittorrent, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Path.Combine(_folder, "setup.exe"));
        var tool = new QbittorrentCompanion(system.Object, new Mock<IConnectionsRepository>().Object, Mock.Of<IInstalledClientLogins>(), downloader.Object);

        await tool.UpdateAsync(new CompanionRunContext("http://127.0.0.1:7879", _folder, Mock.Of<ICompanionProgress>()), CancellationToken.None);

        system.Verify(item => item.StopProcess("qbittorrent"), Times.Once);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(elevatedArguments.Split("-EncodedCommand ")[1]));
        Assert.Contains(CompanionCatalog.Qbittorrent.Sha256, script, StringComparison.OrdinalIgnoreCase);
        system.Verify(item => item.Start(It.Is<string>(path => path.EndsWith("qbittorrent.exe", StringComparison.OrdinalIgnoreCase)), "--confirm-legal-notice"), Times.Once);
    }

    [Fact]
    public async Task A_qBittorrent_download_that_fails_its_checksum_is_never_run_and_the_app_is_left_running()
    {
        var system = new Mock<ICompanionSystem>();
        system.SetupGet(item => item.ProgramFiles).Returns(@"C:\Program Files");
        system.Setup(item => item.FileExists(It.IsAny<string>())).Returns(true);
        var downloader = new Mock<ICompanionDownloader>();
        downloader.Setup(item => item.DownloadAsync(It.IsAny<CompanionArtifact>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CompanionDownloadException("not the build Deluno expects"));
        var tool = new QbittorrentCompanion(system.Object, new Mock<IConnectionsRepository>().Object, Mock.Of<IInstalledClientLogins>(), downloader.Object);

        await Assert.ThrowsAsync<CompanionDownloadException>(() => tool.UpdateAsync(new CompanionRunContext("http://127.0.0.1:7879", _folder, Mock.Of<ICompanionProgress>()), CancellationToken.None));

        system.Verify(item => item.StopProcess(It.IsAny<string>()), Times.Never);
        system.Verify(item => item.RunElevatedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        system.Verify(item => item.Start(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Weir_downloads_its_latest_release_checks_it_against_the_SHA256SUMS_installs_silently_and_waits_for_it_to_answer()
    {
        var setupBytes = new byte[5_100_000];
        Random.Shared.NextBytes(setupBytes);
        using var weir = new TinyWeir();
        var system = WeirSystem(weir.Port);
        var tool = NewWeir(system.Object, Hash(setupBytes), setupBytes);

        await tool.UpdateAsync(new CompanionRunContext("http://127.0.0.1:7879", _folder, Mock.Of<ICompanionProgress>()), CancellationToken.None);

        system.Verify(item => item.RunAsync(It.Is<string>(path => path.EndsWith(CompanionCatalog.WeirSetupAsset, StringComparison.Ordinal)), "--silent", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_Weir_installer_whose_checksum_does_not_match_the_SHA256SUMS_is_refused_and_never_run()
    {
        var setupBytes = new byte[5_100_000];
        Random.Shared.NextBytes(setupBytes);
        using var weir = new TinyWeir();
        var system = WeirSystem(weir.Port);
        var tool = NewWeir(system.Object, new string('A', 64), setupBytes);

        var failure = await Assert.ThrowsAsync<CompanionDownloadException>(() => tool.UpdateAsync(new CompanionRunContext("http://127.0.0.1:7879", _folder, Mock.Of<ICompanionProgress>()), CancellationToken.None));

        Assert.Contains("SHA-256 does not match", failure.Message, StringComparison.Ordinal);
        system.Verify(item => item.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(File.Exists(Path.Combine(_folder, CompanionCatalog.WeirSetupAsset)));
    }

    [Fact]
    public async Task While_GitHubs_API_limit_is_used_up_Weir_is_still_found_through_the_release_feed_release_candidates_included()
    {
        var setupBytes = new byte[5_100_000];
        Random.Shared.NextBytes(setupBytes);
        using var weir = new TinyWeir();
        var system = WeirSystem(weir.Port);
        var requested = new List<string>();
        var tool = NewWeir(system.Object, Hash(setupBytes), setupBytes, apiLimited: true, requested: requested);

        await tool.UpdateAsync(new CompanionRunContext("http://127.0.0.1:7879", _folder, Mock.Of<ICompanionProgress>()), CancellationToken.None);

        // The newest tag by version order is release candidate 13, over rc.12 and the others that the feed lists before it.
        Assert.Contains($"https://github.com/{CompanionCatalog.WeirRepository}/releases/download/{StubGitHub.Newest}/{CompanionCatalog.WeirChecksumsAsset}", requested);
        Assert.Contains($"https://github.com/{CompanionCatalog.WeirRepository}/releases/download/{StubGitHub.Newest}/{CompanionCatalog.WeirSetupAsset}", requested);
        system.Verify(item => item.RunAsync(It.Is<string>(path => path.EndsWith(CompanionCatalog.WeirSetupAsset, StringComparison.Ordinal)), "--silent", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Weir_is_found_through_the_release_feed_first_and_the_API_is_never_asked_when_the_feed_works()
    {
        var setupBytes = new byte[5_100_000];
        Random.Shared.NextBytes(setupBytes);
        using var weir = new TinyWeir();
        var system = WeirSystem(weir.Port);
        var requested = new List<string>();
        var tool = NewWeir(system.Object, Hash(setupBytes), setupBytes, apiLimited: false, requested: requested);

        await tool.UpdateAsync(new CompanionRunContext("http://127.0.0.1:7879", _folder, Mock.Of<ICompanionProgress>()), CancellationToken.None);

        Assert.DoesNotContain(requested, url => url.Contains("api.github.com", StringComparison.Ordinal));
        Assert.Contains($"https://github.com/{CompanionCatalog.WeirRepository}/releases/download/{StubGitHub.Newest}/{CompanionCatalog.WeirSetupAsset}", requested);
    }

    [Fact]
    public async Task The_feed_fallback_keeps_the_checksum_check_as_strict_and_never_runs_a_mismatched_installer()
    {
        var setupBytes = new byte[5_100_000];
        Random.Shared.NextBytes(setupBytes);
        using var weir = new TinyWeir();
        var system = WeirSystem(weir.Port);
        var tool = NewWeir(system.Object, new string('B', 64), setupBytes, apiLimited: true);

        var failure = await Assert.ThrowsAsync<CompanionDownloadException>(() => tool.UpdateAsync(new CompanionRunContext("http://127.0.0.1:7879", _folder, Mock.Of<ICompanionProgress>()), CancellationToken.None));

        Assert.Contains("SHA-256 does not match", failure.Message, StringComparison.Ordinal);
        system.Verify(item => item.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task When_the_limit_is_used_up_and_the_feed_cannot_be_read_either_the_message_names_the_limit_not_the_internet()
    {
        using var weir = new TinyWeir();
        var tool = NewWeir(WeirSystem(weir.Port).Object, new string('A', 64), [1], apiLimited: true, feedAvailable: false);

        var failure = await Assert.ThrowsAsync<CompanionStepException>(() => tool.UpdateAsync(new CompanionRunContext("http://127.0.0.1:7879", _folder, Mock.Of<ICompanionProgress>()), CancellationToken.None));

        Assert.Contains("used up until", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Deluno will try again then", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("internet connection", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_404_from_the_release_feed_names_the_feed_and_never_asks_the_api()
    {
        using var weir = new TinyWeir();
        var requested = new List<string>();
        var tool = NewWeir(WeirSystem(weir.Port).Object, new string('A', 64), [1], feedAvailable: false, feedNotFound: true, requested: requested);

        var failure = await Assert.ThrowsAsync<CompanionStepException>(() => tool.UpdateAsync(new CompanionRunContext("http://127.0.0.1:7879", _folder, Mock.Of<ICompanionProgress>()), CancellationToken.None));

        Assert.Contains("release feed isn't reachable", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("internet connection", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(requested, url => url.Contains("api.github.com", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the newest release has no files (#1264)

    private const string Rc17 = StubGitHub.Rc17;

    /// <summary>The owner in GitHub's recorded Weir replies: the address before Weir moved.</summary>
    private const string RecordedWeirRepository = "jampat000/Weir";

    private static string DownloadUrl(string tag, string asset) => $"https://github.com/{CompanionCatalog.WeirRepository}/releases/download/{tag}/{asset}";

    /// <summary>GitHub's recorded 404 for a release file that was not uploaded.</summary>
    private static HttpResponseMessage RecordedNoFile() => RecordedReplies.Github("weir-newest-release-has-no-files").Find($"{Rc17}/SHA256SUMS.txt").Reply.ToResponse();

    private async Task<(WeirCompanion Tool, Mock<ICompanionSystem> System, TinyWeir Weir)> WeirWith(List<string> requested, bool feedAvailable = true, Func<HttpRequestMessage, HttpResponseMessage?>? own = null)
    {
        var setupBytes = new byte[5_100_000];
        Random.Shared.NextBytes(setupBytes);
        var weir = new TinyWeir();
        var system = WeirSystem(weir.Port);
        await Task.CompletedTask;
        return (NewWeir(system.Object, Hash(setupBytes), setupBytes, feedAvailable: feedAvailable, requested: requested, own: own), system, weir);
    }

    private static CompanionRunContext Context(string folder) => new("http://127.0.0.1:7879", folder, Mock.Of<ICompanionProgress>());

    [Fact]
    public async Task The_newest_release_having_no_files_the_newest_release_that_has_them_is_installed_and_never_the_empty_one()
    {
        var requested = new List<string>();
        var (tool, system, weir) = await WeirWith(requested);
        using var _ = weir;

        await tool.UpdateAsync(Context(_folder), CancellationToken.None);

        Assert.Contains(DownloadUrl(Rc17, CompanionCatalog.WeirChecksumsAsset), requested);
        Assert.DoesNotContain(DownloadUrl(Rc17, CompanionCatalog.WeirSetupAsset), requested);
        Assert.Contains(DownloadUrl(StubGitHub.Newest, CompanionCatalog.WeirSetupAsset), requested);
        system.Verify(item => item.RunAsync(It.Is<string>(path => path.EndsWith(CompanionCatalog.WeirSetupAsset, StringComparison.Ordinal)), "--silent", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task When_the_release_feed_cannot_be_read_the_API_list_is_walked_the_same_way_and_rc13_is_installed()
    {
        var requested = new List<string>();
        var (tool, system, weir) = await WeirWith(requested, feedAvailable: false);
        using var _ = weir;

        await tool.UpdateAsync(Context(_folder), CancellationToken.None);

        Assert.Contains(requested, url => url.Contains("api.github.com/repos/", StringComparison.Ordinal));
        Assert.DoesNotContain(DownloadUrl(Rc17, CompanionCatalog.WeirSetupAsset), requested);
        // The API route follows the download addresses inside GitHub's recorded reply, which was recorded under the address
        // Weir had before it moved (the replay serves it for either). Re-record from the new address to retire this.
        Assert.Contains(DownloadUrl(StubGitHub.Newest, CompanionCatalog.WeirChecksumsAsset).Replace(CompanionCatalog.WeirRepository, RecordedWeirRepository), requested);
        system.Verify(item => item.RunAsync(It.Is<string>(path => path.EndsWith(CompanionCatalog.WeirSetupAsset, StringComparison.Ordinal)), "--silent", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_newer_release_whose_checksum_file_does_not_list_the_installer_is_passed_over_for_the_one_before_it()
    {
        var requested = new List<string>();
        var (tool, system, weir) = await WeirWith(requested, own: request =>
            request.RequestUri!.AbsoluteUri == DownloadUrl(StubGitHub.Newest, CompanionCatalog.WeirChecksumsAsset)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("0000000000000000000000000000000000000000000000000000000000000000  something-else.zip\n") }
                : null);
        using var _ = weir;

        await tool.UpdateAsync(Context(_folder), CancellationToken.None);

        Assert.Contains(DownloadUrl("v1.0.0-rc.12", CompanionCatalog.WeirSetupAsset), requested);
        system.Verify(item => item.RunAsync(It.IsAny<string>(), "--silent", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_release_whose_installer_answers_404_after_its_checksum_arrived_is_passed_over_for_the_one_before_it()
    {
        var requested = new List<string>();
        var (tool, system, weir) = await WeirWith(requested, own: request =>
            request.RequestUri!.AbsoluteUri == DownloadUrl(StubGitHub.Newest, CompanionCatalog.WeirSetupAsset) ? RecordedNoFile() : null);
        using var _ = weir;

        await tool.UpdateAsync(Context(_folder), CancellationToken.None);

        Assert.Contains(DownloadUrl(StubGitHub.Newest, CompanionCatalog.WeirChecksumsAsset), requested);
        Assert.Contains(DownloadUrl("v1.0.0-rc.12", CompanionCatalog.WeirSetupAsset), requested);
        system.Verify(item => item.RunAsync(It.IsAny<string>(), "--silent", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task When_no_release_in_the_window_has_its_files_one_plain_sentence_names_the_newest_looked_at_and_nothing_runs(bool feedAvailable)
    {
        var requested = new List<string>();
        var (tool, system, weir) = await WeirWith(requested, feedAvailable, own: request =>
            request.RequestUri!.AbsoluteUri.Contains("/releases/download/", StringComparison.Ordinal) ? RecordedNoFile() : null);
        using var _ = weir;

        var failure = await Assert.ThrowsAsync<CompanionStepException>(() => tool.UpdateAsync(Context(_folder), CancellationToken.None));

        Assert.Contains(Rc17, failure.Message, StringComparison.Ordinal);
        Assert.Contains("have no Windows installer yet", failure.Message, StringComparison.Ordinal);
        Assert.Contains("try again in a few minutes", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', failure.Message);
        if (feedAvailable)
        {
            // At most the newest five are looked at, one checksum file each.
            Assert.Equal(Deluno.Contracts.ReleaseFallback.MaxReleasesLooked, requested.Count(url => url.EndsWith(CompanionCatalog.WeirChecksumsAsset, StringComparison.Ordinal)));
        }

        system.Verify(item => item.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------------------------ helpers

    private static ConnectionUpdateFacts Facts(string project, string installed, string latest, string? host, string? pinned, bool installedByDeluno)
        => new(project, installed, latest, host, pinned, installedByDeluno, CanInstallCompanions: true);

    private static ConnectionVersionRow Row(string version, string? dismissed = null)
        => new(ConnectionKinds.DownloadClient, "qb1", "qbittorrent", version, DateTimeOffset.UnixEpoch, dismissed);

    private static void SetUpQbittorrent(Mock<IUpdatableCompanion> tool)
    {
        tool.SetupGet(item => item.Project).Returns("qbittorrent");
        tool.SetupGet(item => item.PinnedVersion).Returns("5.2.1");
        tool.Setup(item => item.IsInstalled()).Returns(true);
    }

    private static Mock<IConnectionVersionStore> StoreOver(List<ConnectionVersionRow> rows)
    {
        var store = new Mock<IConnectionVersionStore>();
        store.Setup(item => item.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => rows.ToArray());
        store.Setup(item => item.DismissAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string kind, string id, string _, CancellationToken _) => rows.Any(row => row.Kind == kind && row.Id == id));
        return store;
    }

    private ConnectionUpdateService Service(
        List<ConnectionVersionRow> rows,
        Func<LatestRelease?> latest,
        Mock<IConnectionVersionStore>? store = null,
        IEnumerable<ICompanionTool>? tools = null,
        IConnectionHealthTester? tester = null,
        IActivityFeedRepository? activity = null)
    {
        var releases = new Mock<ILatestReleaseService>();
        releases.Setup(item => item.Peek(It.IsAny<string>())).Returns(() => latest());
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(item => item.ListDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DownloadClientItem("qb1", "qBittorrent", "qbittorrent", "127.0.0.1", 8080, null, null, null, false, null, null, null, null, 10, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(item => item.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new ConnectionUpdateService(
            (store ?? StoreOver(rows)).Object,
            releases.Object,
            connections.Object,
            processors.Object,
            tester ?? Mock.Of<IConnectionHealthTester>(),
            tools ?? [],
            activity ?? Mock.Of<IActivityFeedRepository>(),
            Options.Create(new StoragePathOptions { DataRoot = _folder }),
            NullLogger<ConnectionUpdateService>.Instance)
        {
            CompanionsAvailable = () => true,
            Delay = (_, _) => Task.CompletedTask,
            RecheckAttempts = 2
        };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static Mock<ICompanionSystem> WeirSystem(int port)
    {
        var system = new Mock<ICompanionSystem>();
        system.SetupGet(item => item.LocalAppData).Returns(Path.Combine(Path.GetTempPath(), "weir-update-local"));
        system.Setup(item => item.FileExists(It.IsAny<string>())).Returns(true);
        system.Setup(item => item.ListeningPortsOf(It.IsAny<string>())).Returns([port]);
        system.Setup(item => item.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new CompanionRunResult(0, false));
        return system;
    }

    /// <summary>A Weir whose GitHub release, installer and checksum file are answered by a stub, never the network.</summary>
    private WeirCompanion NewWeir(ICompanionSystem system, string publishedHash, byte[] setupBytes, bool apiLimited = false, bool feedAvailable = true, List<string>? requested = null, bool feedNotFound = false, Func<HttpRequestMessage, HttpResponseMessage?>? own = null)
    {
        // The real client sits behind the GitHub handler, which turns a used-up limit into its plain failure.
        HttpMessageHandler handler = new Deluno.Infrastructure.Http.GitHubApiConditionalHandler(new Deluno.Infrastructure.Http.GitHubApiState(), TimeProvider.System)
        {
            InnerHandler = new StubGitHub(publishedHash, setupBytes, apiLimited, feedAvailable, requested, feedNotFound, own)
        };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new WeirCompanion(
            system,
            new Mock<IProcessorRepository>().Object,
            Mock.Of<IProcessorConnectionService>(),
            new Mock<Deluno.Security.Data.ISecurityRepository>().Object,
            new CompanionDownloader(factory.Object),
            factory.Object);
    }

    /// <summary>
    /// GitHub as it answered about Weir on 14 Oct 2026 (tests/fixtures/recorded/github), with rc.17 left in the feed and the list
    /// exactly as recorded: published with no files, so every file of it answers the recorded 404 (#1264). Apart from that, only
    /// two things are changed on purpose: the feed entries are listed oldest first, so that the version order, not the position,
    /// is what picks the tag; and the checksum file names the installer by the hash of the test's own installer bytes (the one
    /// value changed in the recorded rc.13 file).
    /// </summary>
    private sealed class StubGitHub(string publishedHash, byte[] setupBytes, bool apiLimited = false, bool feedAvailable = true, List<string>? requested = null, bool feedNotFound = false, Func<HttpRequestMessage, HttpResponseMessage?>? own = null) : HttpMessageHandler
    {
        private static readonly Recording Weir = RecordedReplies.Github("weir-newest-release-has-no-files");

        /// <summary>The newest tag with files in the recording.</summary>
        public const string Newest = "v1.0.0-rc.13";

        /// <summary>The newest release in the recording, published with no files.</summary>
        public const string Rc17 = "v1.0.0-rc.17";

        private static string Feed()
        {
            var atom = Weir.Find("Weir/releases.atom").Reply.Body!;
            var entries = System.Text.RegularExpressions.Regex.Matches(atom, "<entry>.*?</entry>", System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(match => match.Value)
                .Reverse();
            return atom[..atom.IndexOf("<entry>", StringComparison.Ordinal)] + string.Join('\n', entries) + "\n</feed>";
        }

        private string Checksums(string tag)
        {
            var recorded = Weir.Find($"{tag}/SHA256SUMS.txt").Reply.Body!;
            return string.Join('\n', recorded.Split('\n').Select(line => line.TrimEnd('\r').EndsWith("  " + CompanionCatalog.WeirSetupAsset, StringComparison.Ordinal) ? $"{publishedHash}  {CompanionCatalog.WeirSetupAsset}\r" : line));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            requested?.Add(url);
            if (own?.Invoke(request) is { } answered)
            {
                return Task.FromResult(answered);
            }

            HttpContent content;
            if (apiLimited && request.RequestUri.Host == "api.github.com")
            {
                var limited = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"API rate limit exceeded"}""") };
                limited.Headers.Add("X-RateLimit-Remaining", "0");
                limited.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());
                return Task.FromResult(limited);
            }

            if (url.EndsWith("/releases.atom", StringComparison.Ordinal))
            {
                if (!feedAvailable)
                {
                    // A feed that cannot be read answers with a bare status (a 503, not recorded); a missing repository gives GitHub's recorded 404.
                    return Task.FromResult(feedNotFound
                        ? RecordedReplies.Github("release-feed-not-found").Single.Reply.ToResponse()
                        : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                content = new StringContent(Feed(), Encoding.UTF8, "application/atom+xml");
            }
            else if (url.Contains("/releases?", StringComparison.Ordinal))
            {
                content = new StringContent(Weir.Find("api.github.com").Reply.Body!, Encoding.UTF8, "application/json");
            }
            else if (url.Contains($"/{Rc17}/", StringComparison.Ordinal))
            {
                // rc.17 was published with no files: GitHub's own recorded 404 for its checksum file answers every file of it.
                return Task.FromResult(Weir.Find($"{Rc17}/SHA256SUMS.txt").Reply.ToResponse());
            }
            else if (url.EndsWith(CompanionCatalog.WeirChecksumsAsset, StringComparison.Ordinal))
            {
                var tag = url.Split("/releases/download/")[1].Split('/')[0];
                content = new StringContent(Checksums(Uri.UnescapeDataString(tag)), Encoding.UTF8, "text/plain");
            }
            else
            {
                content = new ByteArrayContent(setupBytes);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Answers Weir's bootstrap-status route on a free local port, as a running Weir does.</summary>
    private sealed class TinyWeir : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public TinyWeir()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        var body = Encoding.UTF8.GetBytes("""{"bootstrap_allowed":false}""");
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(body);
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });
        }

        public int Port { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
