using Deluno.Downloader.Nzb.Nntp;
using Deluno.Downloader.Nzb.Yenc;
using Deluno.Downloader.Tests.Nzb.Nntp;

namespace Deluno.Downloader.Tests.Nzb;

public class NntpConnectionTests
{
    private static NntpServerOptions Opt(int port, string? user = null, string? pass = null)
        => new("s1", "test", "127.0.0.1", port, UseTls: false, user, pass);

    [Fact]
    public async Task Connects_and_quits_cleanly()
    {
        await using var server = FakeNntpServer.Start();
        await using var conn = await NntpConnection.ConnectAsync(Opt(server.Port));
        await conn.QuitAsync();
        Assert.Contains("QUIT", server.ReceivedCommands);
    }

    [Fact]
    public async Task Probes_capabilities_at_connect()
    {
        await using var server = FakeNntpServer.Start();
        await using var conn = await NntpConnection.ConnectAsync(Opt(server.Port));
        Assert.Contains("CAPABILITIES", server.ReceivedCommands);
        // MODE READER is sent because the fake server advertises MODE-READER.
        Assert.Contains(server.ReceivedCommands, c => c.StartsWith("MODE READER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Authenticates_with_user_and_pass()
    {
        await using var server = FakeNntpServer.Start();
        server.RequireAuth = true;
        server.ExpectedUser = "alice";
        server.ExpectedPass = "s3cret";

        await using var conn = await NntpConnection.ConnectAsync(Opt(server.Port, "alice", "s3cret"));

        Assert.Contains("AUTHINFO USER alice", server.ReceivedCommands);
        Assert.Contains("AUTHINFO PASS s3cret", server.ReceivedCommands);
    }

    [Fact]
    public async Task Auth_failure_throws_typed_exception()
    {
        await using var server = FakeNntpServer.Start();
        server.RequireAuth = true;
        server.ExpectedUser = "alice";
        server.ExpectedPass = "right";

        await Assert.ThrowsAsync<NntpAuthenticationException>(async () =>
        {
            await using var _ = await NntpConnection.ConnectAsync(Opt(server.Port, "alice", "wrong"));
        });
    }

    [Fact]
    public async Task Fetches_body_and_yenc_round_trip_succeeds()
    {
        await using var server = FakeNntpServer.Start();
        var payload = new byte[5000];
        new Random(123).NextBytes(payload);
        server.Articles["msg-1@x"] = YEncTestEncoder.EncodeSinglePart("blob.bin", payload);

        await using var conn = await NntpConnection.ConnectAsync(Opt(server.Port));
        var raw = await conn.FetchBodyAsync("msg-1@x", CancellationToken.None);
        var article = YEncDecoder.Decode(raw);

        Assert.Equal(payload, article.Payload);
        Assert.Contains("BODY <msg-1@x>", server.ReceivedCommands);
    }

    [Fact]
    public async Task Missing_article_throws_typed_exception()
    {
        await using var server = FakeNntpServer.Start();
        server.Missing.Add("gone@x");
        await using var conn = await NntpConnection.ConnectAsync(Opt(server.Port));
        var ex = await Assert.ThrowsAsync<NntpArticleNotFoundException>(
            () => conn.FetchBodyAsync("gone@x", CancellationToken.None));
        Assert.Equal("gone@x", ex.MessageId);
    }

    [Fact]
    public async Task Dot_stuffed_lines_are_unstuffed_on_client()
    {
        await using var server = FakeNntpServer.Start();
        var body = ".this line starts with a dot\r\nordinary line\r\n"u8.ToArray();
        server.Articles["dot@x"] = body;

        await using var conn = await NntpConnection.ConnectAsync(Opt(server.Port));
        var raw = await conn.FetchBodyAsync("dot@x", CancellationToken.None);
        var text = System.Text.Encoding.ASCII.GetString(raw);

        Assert.Contains(".this line starts with a dot", text);
        Assert.DoesNotContain("..this", text);
    }

    [Fact]
    public async Task DATE_command_returns_111()
    {
        await using var server = FakeNntpServer.Start();
        await using var conn = await NntpConnection.ConnectAsync(Opt(server.Port));
        // HealthCheckAsync sends DATE and returns true iff code is 111.
        var ok = await conn.HealthCheckAsync(CancellationToken.None);
        Assert.True(ok);
        Assert.Contains("DATE", server.ReceivedCommands);
    }
}
