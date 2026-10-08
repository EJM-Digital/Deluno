using System.Net;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

public sealed class TrayComponentRecoveryNoticesTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "deluno-recovery-notices-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch { /* temp folder */ }
    }

    private async Task WriteNotice()
        => await ComponentStateMigration.RunAsync(Path.Combine(_data, "install"), _data, ["transmission"], null, null, 9091, () => new Fake(), null, CancellationToken.None);

    private sealed class Fake : HttpMessageHandler
    {
        public List<string> Added { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains("X-Transmission-Session-Id"))
            {
                var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                conflict.Headers.Add("X-Transmission-Session-Id", "s");
                return Task.FromResult(conflict);
            }

            var text = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            if (text.Contains("torrent-add")) Added.Add(text);
            var arguments = text.Contains("session-get")
                ? "{\"download-dir\":\"/d\",\"incomplete-dir\":\"/i\",\"incomplete-dir-enabled\":true}"
                : text.Contains("torrent-get")
                    ? "{\"torrents\":[{\"name\":\"B\",\"magnetLink\":\"magnet:?xt=urn:btih:bbb\",\"downloadDir\":\"/d\"}]}"
                    : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":\"success\",\"arguments\":" + arguments + "}") });
        }
    }

    [Fact]
    public async Task The_notice_is_listed_in_plain_words_added_back_through_the_tray_and_cleared()
    {
        await WriteNotice();
        var handler = new Fake();
        var restarts = 0;
        var notices = new TrayComponentRecoveryNotices(() => _data, null, spec => new TransmissionRpc(spec.DefaultPort, null, () => handler), (_, _) => Task.FromResult(++restarts > 0));

        var listed = Assert.Single(await notices.ListAsync(CancellationToken.None));
        Assert.Equal("Transmission's settings were rebuilt after Deluno was reinstalled; 1 torrent may need adding back.", listed.Message);

        var result = await notices.ReaddAsync("transmission", CancellationToken.None);

        Assert.True(result!.Done);
        Assert.Equal(1, restarts);
        Assert.Single(handler.Added);
        Assert.Empty(await notices.ListAsync(CancellationToken.None));
        Assert.Null(await notices.ReaddAsync("transmission", CancellationToken.None));
    }

    [Fact]
    public async Task Dismissing_clears_the_notice_without_adding_anything()
    {
        await WriteNotice();
        var notices = new TrayComponentRecoveryNotices(() => _data);

        Assert.False(await notices.DismissAsync("qbittorrent", CancellationToken.None));
        Assert.True(await notices.DismissAsync("transmission", CancellationToken.None));
        Assert.Empty(await notices.ListAsync(CancellationToken.None));
    }
}
