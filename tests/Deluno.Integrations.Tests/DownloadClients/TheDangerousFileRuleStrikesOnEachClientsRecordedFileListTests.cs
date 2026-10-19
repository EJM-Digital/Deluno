using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// The dangerous or junk file rule (cleanup sheet C6) reads what the client really lists inside a download. For each torrent client
/// the file names come from the client's own recorded reply through its real adapter (qBittorrent 5.2.1, Transmission 4.1.3, Deluge
/// 2.2.0); they are handed to the real health evaluator, and a blocked name is one instant strike while a clean listing is left alone.
/// SABnzbd 5.1.3 and NZBGet 26.3 (a paused job of a film and an .nfo, recorded in Docker) and rTorrent 0.16.25 (a finished folder of three
/// episodes) are here too. uTorrent is not: its listing needs the Windows runner (tests/fixtures/recorded/MISSING.md).
/// </summary>
public sealed class TheDangerousFileRuleStrikesOnEachClientsRecordedFileListTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    private static async Task<IReadOnlyList<string>> RecordedFilesAsync(string client)
    {
        IReadOnlyList<string>? files;
        switch (client)
        {
            case "qbittorrent":
            {
                var handler = RecordedReplies.Handler("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("finished-folder")]);
                files = await new QbittorrentDownloadClient(() => handler)
                    .GetFilesAsync(Client("qbittorrent", "deluno"), "3b1d84012faffbb5c3c8d00d92358d52f2c856eb", CancellationToken.None);
                break;
            }

            case "transmission":
            {
                var handler = new Calls("transmission", [RecordedReplies.Transmission("session-handshake"), RecordedReplies.Transmission("finished-folder")]).Build();
                files = await new TransmissionDownloadClient(new Factory(handler)).GetFilesAsync(Client("transmission"), "52", CancellationToken.None);
                break;
            }

            case "sabnzbd":
            {
                // The job's own recorded exchanges: the id the client gave, then its get_files reply.
                var recording = RecordedReplies.Sabnzbd("files-of-a-paused-job");
                var handler = RecordedReplies.Handler("sabnzbd", [recording]);
                files = await new SabnzbdDownloadClient(new Factory(handler))
                    .GetFilesAsync(Client("sabnzbd", secret: "key"), NzoIdOf(recording), CancellationToken.None);
                break;
            }

            case "nzbget":
            {
                var recording = RecordedReplies.Nzbget("files-of-a-paused-job");
                var handler = RecordedReplies.Handler("nzbget", [recording]);
                files = await new NzbGetDownloadClient(new Factory(handler))
                    .GetFilesAsync(Client("nzbget", "nzbget"), "4", CancellationToken.None);
                break;
            }

            case "rtorrent":
            {
                // rTorrent 0.16.25's f.multicall for a finished release folder of three episodes.
                var handler = RecordedReplies.Handler("rtorrent", [RecordedReplies.Rtorrent("finished-folder")]);
                files = await new RtorrentDownloadClient(new Factory(handler))
                    .GetFilesAsync(Client("rtorrent"), "C703E0C4CD781E6772DDF298A5486ABA003F5353", CancellationToken.None);
                break;
            }

            default:
            {
                var handler = new Calls("deluge", [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("location-moved")]).Build();
                files = await new DelugeDownloadClient(new Factory(handler)).GetFilesAsync(Client("deluge"), "d8185091144732e990e3595f7db0e4410b1d9424", CancellationToken.None);
                break;
            }
        }

        Assert.NotNull(files);
        Assert.NotEmpty(files);
        return files;
    }

    private static string NzoIdOf(Recording recording)
        => System.Text.Json.Nodes.JsonNode.Parse(recording.Exchanges[0].Reply.Body!)!["nzo_ids"]![0]!.GetValue<string>();

    private static DownloadHealthRule DangerousFileRule(string appliesTo)
        => new(
            Id: "dangerous-file", Kind: DownloadHealthRuleKinds.DangerousFile, Enabled: true, StrikeLimit: 1, TimerMinutes: 1,
            AppliesTo: appliesTo, MinCompletionPercent: 0, MaxCompletionPercent: 100, ResetStrikesOnProgress: true,
            ActionPreset: DownloadHealthActionPresets.Replace, CreatedUtc: Now, UpdatedUtc: Now);

    private static ContentBlocklistSettings Blocklist(params string[] patterns)
        => new(Enabled: true, Mode: ContentBlocklistModes.Blacklist, UseBuiltInPatterns: false, CustomPatterns: patterns, RemoteUrl: null, RemotePatterns: [], LastRefreshedUtc: null, UpdatedUtc: Now);

    private static RecoveryQueueSnapshot Snapshot(IReadOnlyList<string> files, bool usenet = false)
        => new(
            Status: "downloading", ErrorMessage: null, SourcePath: null, SpeedMbps: 5, AddedUtc: Now.AddMinutes(-10), EtaSeconds: 60,
            ReleaseName: "Recorded.Folder.Film.2008.1080p.WEB-DL.x264-GOLDEN", Peers: 5, IsUsenetProtocol: usenet, Files: files);

    // What each client really listed, and a pattern for a name in that list (the small extra file in a folder; the film for Deluge's single file).
    [Theory]
    [InlineData("qbittorrent", "*.nfo")]
    [InlineData("transmission", "*.nfo")]
    [InlineData("deluge", "*.mkv")]
    [InlineData("sabnzbd", "*.nfo")]
    [InlineData("nzbget", "*.nfo")]
    [InlineData("rtorrent", "*S01E02.mkv")]
    public async Task A_blocked_file_in_the_clients_own_file_list_is_one_instant_strike(string client, string pattern)
    {
        var files = await RecordedFilesAsync(client);
        var blocked = files.First(name => name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase));

        var findings = new DownloadHealthEvaluator().Evaluate(Snapshot(files), Now, [DangerousFileRule(DownloadHealthAppliesTo.Both)], Blocklist(pattern));

        var strike = Assert.Single(findings, finding => finding.Kind == DownloadHealthStrikes.DangerousFile);
        Assert.Equal(1, strike.RuleStrikeLimit);
        Assert.Equal("dangerous-file", strike.RuleId);
        Assert.Contains(blocked, strike.Evidence, StringComparison.Ordinal);
        Assert.Equal(DownloadHealthActionPresets.Replace, strike.RuleActionPreset);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    [InlineData("rtorrent")]
    public async Task A_listing_with_no_blocked_name_is_left_alone(string client)
    {
        var files = await RecordedFilesAsync(client);

        var findings = new DownloadHealthEvaluator().Evaluate(Snapshot(files), Now, [DangerousFileRule(DownloadHealthAppliesTo.Both)], Blocklist("*.exe", "*.scr"));

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.DangerousFile);
        Assert.DoesNotContain(findings, finding => finding.Kind == "content-blocklist-match");
    }

    [Theory]
    [InlineData("qbittorrent", "*.nfo", DownloadHealthAppliesTo.Usenet)]
    [InlineData("transmission", "*.nfo", DownloadHealthAppliesTo.Usenet)]
    [InlineData("deluge", "*.mkv", DownloadHealthAppliesTo.Usenet)]
    [InlineData("rtorrent", "*S01E02.mkv", DownloadHealthAppliesTo.Usenet)]
    public async Task A_usenet_only_rule_does_not_strike_a_torrent_whose_file_list_is_blocked_though_the_warning_stays(string client, string pattern, string scope)
    {
        var files = await RecordedFilesAsync(client);

        var findings = new DownloadHealthEvaluator().Evaluate(Snapshot(files), Now, [DangerousFileRule(scope)], Blocklist(pattern));

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.DangerousFile);
        Assert.Contains(findings, finding => finding.Kind == "content-blocklist-match");
    }

    [Theory]
    [InlineData("qbittorrent", "*.nfo")]
    [InlineData("transmission", "*.nfo")]
    [InlineData("deluge", "*.mkv")]
    [InlineData("sabnzbd", "*.nfo")]
    [InlineData("nzbget", "*.nfo")]
    public async Task A_torrents_only_rule_does_not_strike_a_usenet_item_with_the_same_blocked_list(string client, string pattern)
    {
        var files = await RecordedFilesAsync(client);

        var findings = new DownloadHealthEvaluator().Evaluate(Snapshot(files, usenet: true), Now, [DangerousFileRule(DownloadHealthAppliesTo.Torrents)], Blocklist(pattern));

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.DangerousFile);
        var instant = new DownloadHealthEvaluator().Evaluate(Snapshot(files, usenet: true), Now, [DangerousFileRule(DownloadHealthAppliesTo.Usenet)], Blocklist(pattern));
        Assert.Contains(instant, finding => finding.Kind == DownloadHealthStrikes.DangerousFile);
    }
}
