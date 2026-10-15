using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Where each download client really puts a category, and when it has
/// finished (#616, #635).
///
/// <para><b>Why this file exists.</b> Only SABnzbd and qBittorrent could say
/// where a category saves, and SABnzbd said it relative ("Movies"). NZBGet,
/// Deluge and Transmission said nothing. And NZBGet's finished downloads were
/// never seen as finished: its history reads "SUCCESS/UNPACK".</para>
/// </summary>
public sealed class ClientFoldersAreReadTests
{
    [Theory]
    [InlineData("SUCCESS/UNPACK", "completed")]
    [InlineData("SUCCESS/ALL", "completed")]
    // #910: a post-processing script problem is the user's script, not the
    // release, so it is its own outcome rather than a folded-in success or a
    // bad-release failure (see DownloadClientHistoryOutcomes.ScriptWarning).
    [InlineData("WARNING/SCRIPT", "scriptWarning")]
    [InlineData("FAILURE/PAR", "failed")]
    [InlineData("WARNING/DAMAGED", "failed")]
    [InlineData("DELETED/MANUAL", "failed")]
    public async Task An_NZBGet_history_row_is_finished_or_failed_by_its_main_status(string status, string outcome)
    {
        var history = await RecordedNzbGetHistory(status);

        Assert.Equal(outcome, Assert.Single(history).Outcome);
    }

    [Fact]
    public async Task An_NZBGet_download_is_in_its_own_folder_not_one_with_its_name_added_again()
    {
        var history = await RecordedNzbGetHistory("SUCCESS/UNPACK");

        Assert.Equal(@"C:\Downloads\Completed\TV\Pioneer.One.S01E01.1080p", Assert.Single(history).SourcePath);
    }

    [Fact]
    public async Task NZBGet_names_a_categorys_folder_with_its_variables_filled_in()
    {
        // NZBGet 26.3's recorded configuration, with the TV category's folder written as a variable of NZBGet's own.
        var result = await new NzbGetDownloadClient(Factory(RecordedNzbGetConfig(("result.[Name=Category2.DestDir].Value", @"${DestDir}\TV"))))
            .CheckCategoryAsync(Client("nzbget"), "TV", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, result.Status);
        Assert.Equal(@"C:\Downloads\Completed\TV", result.SavePath);
    }

    [Fact]
    public async Task NZBGet_without_the_category_says_where_its_downloads_go_instead()
    {
        // The recorded configuration with no category called TV.
        var result = await new NzbGetDownloadClient(Factory(RecordedNzbGetConfig(("result.[Name=Category2.Name].Value", "Other"))))
            .CheckCategoryAsync(Client("nzbget"), "TV", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Missing, result.Status);
        Assert.Contains(@"C:\Downloads\Completed", result.Message);
    }

    [Fact]
    public async Task Transmission_saves_a_category_inside_its_download_folder()
    {
        var result = await new TransmissionDownloadClient(Factory(RecordedTransmissionSession(("arguments.rpc-version", 16))))
            .CheckCategoryAsync(Client("transmission"), "TV", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, result.Status);
        Assert.Equal(@"C:\Downloads\Completed\TV", result.SavePath);
    }

    /// <summary>#973: labels need Transmission's RPC 16 (2.40); an older daemon gets the same kind of explicit message Deluge gives for its Label plugin being off.</summary>
    [Fact]
    public async Task A_Transmission_too_old_for_labels_says_so_by_name_and_version()
    {
        var result = await new TransmissionDownloadClient(Factory(RecordedTransmissionSession(("arguments.rpc-version", 15))))
            .CheckCategoryAsync(Client("transmission"), "TV", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, result.Status);
        Assert.False(result.Supported);
        Assert.Contains("RPC 15", result.Message);
        Assert.Contains("RPC 16", result.Message);
    }

    /// <summary>A daemon so old it does not even answer rpc-version reads the same as too-old, not as ready.</summary>
    [Fact]
    public async Task A_Transmission_with_no_rpc_version_at_all_is_not_assumed_to_support_labels()
    {
        var result = await new TransmissionDownloadClient(Factory(RecordedTransmissionSession(("arguments.rpc-version", null))))
            .CheckCategoryAsync(Client("transmission"), "TV", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, result.Status);
        Assert.False(result.Supported);
    }

    [Fact]
    public async Task A_Deluge_label_that_moves_finished_downloads_saves_to_its_own_folder()
    {
        var result = await new DelugeDownloadClient(Factory(RecordedDelugeRig.FolderCheck(labelMoves: true, @"C:\Downloads\Completed\Movies"))).CheckCategoryAsync(Client("deluge"), "Movies", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, result.Status);
        Assert.Equal(@"C:\Downloads\Completed\Movies", result.SavePath);
    }

    [Fact]
    public async Task A_Deluge_label_with_no_folder_of_its_own_is_named_with_where_it_goes()
    {
        var result = await new DelugeDownloadClient(Factory(RecordedDelugeRig.FolderCheck(labelMoves: false, string.Empty))).CheckCategoryAsync(Client("deluge"), "Movies", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Configuration, result.Status);
        Assert.Equal(@"C:\Downloads\Completed", result.SavePath);
        Assert.Contains("Move completed to", result.Message);
    }

    [Theory]
    [InlineData(@"C:\Downloads\Completed", "Movies", @"C:\Downloads\Completed\Movies")]
    [InlineData("/downloads/complete", "tv", "/downloads/complete/tv")]
    [InlineData(@"C:\Downloads\Completed", @"D:\Elsewhere\TV", @"D:\Elsewhere\TV")]
    [InlineData("/downloads", "/mnt/tv/", "/mnt/tv")]
    [InlineData(@"C:\Downloads\Completed\", null, @"C:\Downloads\Completed")]
    public void A_clients_folder_is_joined_the_way_the_client_writes_paths(string root, string? folder, string expected)
        => Assert.Equal(expected, ClientPaths.ClientFolder(root, folder));

    /// <summary>NZBGet 26.3's recorded history of one finished job, in the status and folder under test.</summary>
    private static async Task<IReadOnlyList<DownloadClientHistoryItem>> RecordedNzbGetHistory(string status)
    {
        var history = RecordedReplies.Nzbget("finished-single-file").Body(
            "history\"",
            ("^result", 1), ("result.0.Status", status), ("result.0.Category", "TV"),
            ("result.0.NZBName", "Pioneer.One.S01E01.1080p"), ("result.0.Name", "Pioneer.One.S01E01.1080p"),
            ("result.0.DestDir", @"C:\Downloads\Completed\TV\Pioneer.One.S01E01.1080p"));
        return await new NzbGetDownloadClient(Factory(new FixedReply(history))).GetHistoryAsync(Client("nzbget"), DateTimeOffset.UtcNow, CancellationToken.None);
    }

    /// <summary>NZBGet 26.3's recorded configuration, with the given options changed.</summary>
    private static HttpMessageHandler RecordedNzbGetConfig(params (string Path, object? Value)[] edits)
        => new FixedReply(RecordedReplies.Nzbget("settings").Body("\"config\"", edits));

    /// <summary>Transmission 4.1.3's recorded session settings, with the given values changed.</summary>
    private static HttpMessageHandler RecordedTransmissionSession(params (string Path, object? Value)[] edits)
        => new FixedReply(RecordedReplies.Transmission("settings").Body("session-get", edits));

    private static DownloadClientItem Client(string protocol)
        => new("client-1", protocol, protocol, "localhost", 1234, "user", "secret", "http://localhost:1234/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IHttpClientFactory Factory(HttpMessageHandler handler) => new StubFactory(handler);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A client that answers every call with the one recorded reply.</summary>
    private sealed class FixedReply(string reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(RecordedReplies.Json(reply));
    }
}
