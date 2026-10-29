using System.Net;
using System.Text.Json;
using Deluno.Tests.Recorded;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1301: the tray's calls to the Transmission it installed (the few it needs to add torrents back after a folder was rewritten), run
/// against what Transmission 4 really answered: the 409 session handshake, a refused login, a refused connection, its settings, its
/// torrent list and its add replies. The only edit is a magnet link in the torrent list (the recording asked for other fields), and
/// the test says so.
/// </summary>
public sealed class TransmissionRecoveryOnRecordedRepliesTests : IDisposable
{
    private const string Magnet = "magnet:?xt=urn:btih:04d6d32adaa36ddd9877f0734a7027a4a0055042";
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "deluno-tray-recorded-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(dataRoot, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    private static TransmissionRpc Rpc(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept, params Recording[] recordings)
        => new(9091, null, () => RecordedReplies.Handler("transmission", recordings, intercept));

    private static TransmissionRpc RpcOn(Func<HttpMessageHandler> handler) => new(9091, null, handler);

    /// <summary>The recorded torrent list with a magnet link added to its first torrent: the one value this test is about.</summary>
    private static Func<HttpRequestMessage, string, HttpResponseMessage?> ListWithAMagnet(string state)
        => (_, body) => body.Contains("\"torrent-get\"", StringComparison.Ordinal)
            ? RecordedReplies.Transmission(state).Response("torrent-get", ("arguments.torrents.0.magnetLink", Magnet))
            : null;

    [Fact]
    public async Task A_conflict_with_a_session_id_is_answered_again_with_that_id_and_the_second_answer_is_returned()
    {
        var recorded = RecordedReplies.Transmission("session-handshake");
        Assert.Equal(409, recorded.Exchanges[0].Reply.Status);

        var answer = await RpcOn(() => recorded.Sequence()).CallAsync("session-get", new Dictionary<string, object>(), CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal("success", answer!.Value.GetProperty("result").GetString());
        Assert.Equal(recorded.Exchanges[1].Reply.Json()["arguments"]!["download-dir"]!.GetValue<string>(), answer.Value.GetProperty("arguments").GetProperty("download-dir").GetString());
    }

    [Fact]
    public async Task A_refused_login_and_a_client_that_is_not_there_give_no_answer_not_a_wrong_one()
    {
        var refused = await Rpc(null, RecordedReplies.Transmission("login-refused")).CallAsync("session-get", new Dictionary<string, object>(), CancellationToken.None);
        var off = RecordedReplies.Transmission("not-answering");
        var gone = await RpcOn(off.NotAnswering).CallAsync("session-get", new Dictionary<string, object>(), CancellationToken.None);
        var folders = await RpcOn(off.NotAnswering).ReadFoldersAsync(CancellationToken.None);
        var torrents = await RpcOn(off.NotAnswering).ReadTorrentsAsync(CancellationToken.None);

        Assert.Equal(401, RecordedReplies.Transmission("login-refused").Exchanges[0].Reply.Status);
        Assert.Null(refused);
        Assert.Null(gone);
        Assert.Null(folders);
        Assert.Null(torrents);
    }

    [Fact]
    public async Task The_folders_the_client_is_using_are_read_from_its_settings_reply()
    {
        var recorded = RecordedReplies.Transmission("settings");
        var arguments = recorded.Find("session-get").Reply.Json()["arguments"]!;

        var folders = await Rpc(null, recorded).ReadFoldersAsync(CancellationToken.None);

        Assert.NotNull(folders);
        Assert.Equal(arguments["download-dir"]!.GetValue<string>(), folders!.Value.DownloadDir);
        Assert.Equal(arguments["incomplete-dir"]!.GetValue<string>(), folders.Value.IncompleteDir);
        Assert.Equal(arguments["incomplete-dir-enabled"]!.GetValue<bool>(), folders.Value.IncompleteEnabled);
    }

    [Fact]
    public async Task The_torrents_the_client_holds_are_listed_with_their_magnet_links_and_one_with_no_link_is_left_out()
    {
        var recorded = RecordedReplies.Transmission("idle");

        var torrents = await Rpc(ListWithAMagnet("idle"), recorded).ReadTorrentsAsync(CancellationToken.None);
        var unedited = await Rpc(null, recorded).ReadTorrentsAsync(CancellationToken.None);

        var first = recorded.Find("torrent-get").Reply.Json()["arguments"]!["torrents"]![0]!;
        Assert.NotNull(torrents);
        var one = Assert.Single(torrents!, torrent => torrent.MagnetLink == Magnet);
        Assert.Equal(first["name"]!.GetValue<string>(), one.Name);
        Assert.Equal(first["downloadDir"]!.GetValue<string>(), one.DownloadDir);
        Assert.Empty(unedited!);
    }

    [Fact]
    public async Task A_torrent_is_added_back_with_its_folder_and_a_duplicate_counts_as_added_and_a_refused_one_does_not()
    {
        var added = await Rpc(null, RecordedReplies.Transmission("finished-single-file")).AddAsync(new TorrentToReadd("Film", Magnet, "C:\\Downloads\\Movies"), CancellationToken.None);
        var duplicate = await Rpc(null, RecordedReplies.Transmission("duplicate-add")).AddAsync(new TorrentToReadd("Film", Magnet, null), CancellationToken.None);
        var refused = await Rpc(null, RecordedReplies.Transmission("add-refused")).AddAsync(new TorrentToReadd("Film", "not a magnet", null), CancellationToken.None);

        Assert.True(added);
        Assert.True(duplicate);
        Assert.False(refused);
    }

    private void WriteNotice(TransmissionReaddNotice notice)
    {
        var path = ComponentStateMigration.ReaddNoticePath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(notice));
    }

    [Fact]
    public async Task The_notice_is_listed_the_torrents_are_added_back_and_the_notice_goes_and_it_can_be_dismissed_without_adding()
    {
        WriteNotice(new TransmissionReaddNotice(DateTimeOffset.UtcNow, DaemonNeedsRestart: true, [new TorrentToReadd("Film", Magnet, null)]));
        var restarts = 0;
        var notices = new TrayComponentRecoveryNotices(
            () => dataRoot, _ => null,
            _ => Rpc(null, RecordedReplies.Transmission("finished-single-file")),
            (_, _) => Task.FromResult(++restarts > 0));

        var listed = await notices.ListAsync(CancellationToken.None);
        var readded = await notices.ReaddAsync("transmission", CancellationToken.None);

        var notice = Assert.Single(listed);
        Assert.Equal("Transmission", notice.DisplayName);
        Assert.Equal(1, notice.ItemCount);
        Assert.True(notice.CanReadd);
        Assert.Equal(1, restarts);
        Assert.NotNull(readded);
        Assert.True(readded!.Done);
        Assert.Equal("Deluno added 1 torrents back to Transmission.", readded.Message);
        Assert.Empty(await notices.ListAsync(CancellationToken.None));
        Assert.Null(await notices.ReaddAsync("transmission", CancellationToken.None));

        WriteNotice(new TransmissionReaddNotice(DateTimeOffset.UtcNow, DaemonNeedsRestart: false, []));
        Assert.True(await notices.DismissAsync("transmission", CancellationToken.None));
        Assert.Empty(await notices.ListAsync(CancellationToken.None));
        Assert.False(await notices.DismissAsync("transmission", CancellationToken.None));
    }

    [Fact]
    public async Task With_no_torrents_noted_the_list_is_read_from_the_running_client_and_a_client_that_is_off_keeps_the_notice()
    {
        WriteNotice(new TransmissionReaddNotice(DateTimeOffset.UtcNow, DaemonNeedsRestart: false, []));
        var running = await TransmissionReadd.RunAsync(dataRoot, Rpc(ListWithAMagnet("idle"), RecordedReplies.Transmission("idle"), RecordedReplies.Transmission("finished-single-file")), _ => Task.FromResult(true), CancellationToken.None);
        Assert.Equal("Deluno added 1 torrents back to Transmission.", running);
        Assert.Null(TransmissionReadd.Pending(dataRoot));

        WriteNotice(new TransmissionReaddNotice(DateTimeOffset.UtcNow, DaemonNeedsRestart: false, []));
        var off = await TransmissionReadd.RunAsync(dataRoot, RpcOn(RecordedReplies.Transmission("not-answering").NotAnswering), _ => Task.FromResult(true), CancellationToken.None);
        Assert.Contains("Transmission is not running", off);
        Assert.NotNull(TransmissionReadd.Pending(dataRoot));
    }
}
