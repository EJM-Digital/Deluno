using System.Net;
using System.Text;
using Deluno.Tests.Recorded;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1301: the tray's real download of an installer or its checksum file (<see cref="HttpComponentDownloader"/>, no stand-in for
/// the downloader), run against what github.com answered for Weir's release files on 14 Oct 2026. A loopback server replays the
/// recorded reply (status and body) at the address the downloader is asked for, because the downloader takes its address as an argument.
/// </summary>
public sealed class HttpComponentDownloaderOnRecordedGithubTests : IAsyncLifetime
{
    private HttpListener? listener;
    private Task? serving;
    private RecordedReply reply = RecordedReplies.Github("weir-newest-release-has-no-files").Find("rc.13/SHA256SUMS.txt").Reply;
    private string address = string.Empty;

    public Task InitializeAsync()
    {
        var port = FreePort();
        address = $"http://127.0.0.1:{port}/";
        listener = new HttpListener();
        listener.Prefixes.Add(address);
        listener.Start();
        serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                context.Response.StatusCode = reply.Status;
                var body = Encoding.UTF8.GetBytes(reply.Body ?? string.Empty);
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        listener?.Close();
        if (serving is not null)
        {
            await serving;
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task A_file_github_serves_is_saved_whole_under_the_suggested_name_and_its_path_is_returned()
    {
        var download = await new HttpComponentDownloader().DownloadWithStatusAsync(address + "SHA256SUMS.txt", "SHA256SUMS.txt", CancellationToken.None);

        Assert.NotNull(download.Path);
        Assert.False(download.NotFound);
        Assert.EndsWith("-SHA256SUMS.txt", download.Path, StringComparison.Ordinal);
        Assert.Equal(reply.Body, File.ReadAllText(download.Path!));
        File.Delete(download.Path!);
    }

    [Fact]
    public async Task A_file_github_answers_404_for_comes_back_as_not_found_and_no_file_is_left()
    {
        reply = RecordedReplies.Github("weir-newest-release-has-no-files").Find("rc.17/SHA256SUMS.txt").Reply;
        Assert.Equal(404, reply.Status);

        var download = await new HttpComponentDownloader().DownloadWithStatusAsync(address + "SHA256SUMS.txt", "SHA256SUMS.txt", CancellationToken.None);

        Assert.Null(download.Path);
        Assert.True(download.NotFound);
    }

    [Fact]
    public async Task The_plain_download_gives_the_path_of_the_saved_file_and_nothing_for_a_missing_one()
    {
        var saved = await new HttpComponentDownloader().DownloadAsync(address + "SHA256SUMS.txt", "SHA256SUMS.txt", CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(reply.Body, File.ReadAllText(saved!));
        File.Delete(saved!);

        reply = RecordedReplies.Github("weir-newest-release-has-no-files").Find("rc.17/SHA256SUMS.txt").Reply;
        Assert.Null(await new HttpComponentDownloader().DownloadAsync(address + "SHA256SUMS.txt", "SHA256SUMS.txt", CancellationToken.None));
    }

    [Fact]
    public async Task A_server_that_is_not_there_is_not_a_missing_file_so_the_next_release_is_not_tried_for_it()
    {
        listener!.Close();

        var download = await new HttpComponentDownloader().DownloadWithStatusAsync(address + "SHA256SUMS.txt", "SHA256SUMS.txt", CancellationToken.None);

        Assert.Null(download.Path);
        Assert.False(download.NotFound);
    }
}
