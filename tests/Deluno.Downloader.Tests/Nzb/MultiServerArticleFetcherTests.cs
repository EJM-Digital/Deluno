using Deluno.Downloader.Nzb.MultiServer;
using Deluno.Downloader.Nzb.Nntp;
using Deluno.Downloader.Nzb.Yenc;
using Deluno.Downloader.Tests.Nzb.Nntp;

namespace Deluno.Downloader.Tests.Nzb;

public class MultiServerArticleFetcherTests
{
    private static NntpServerOptions ServerAt(
        int port, string id, NntpServerTier tier = NntpServerTier.Primary, int priority = 0)
        => new(id, id, "127.0.0.1", port, UseTls: false, Tier: tier, Priority: priority);

    [Fact]
    public async Task Returns_body_from_first_server_when_present()
    {
        await using var primary = FakeNntpServer.Start();
        primary.Articles["a@x"] = YEncTestEncoder.EncodeSinglePart("x.bin", new byte[] { 1, 2, 3 });

        await using var pool = new NntpConnectionPool(ServerAt(primary.Port, "primary"));
        var fetcher = new MultiServerArticleFetcher([pool]);

        var raw = await fetcher.FetchAsync("a@x", articleDate: null, CancellationToken.None);
        Assert.NotEmpty(raw);
    }

    [Fact]
    public async Task Walks_to_next_tier_when_primary_returns_430()
    {
        // Primary doesn't have the article (430). Fill does. The fetcher
        // must escalate from Primary → Fill without surfacing the 430 as
        // failure. This is the SAB-critical behaviour.
        await using var primary = FakeNntpServer.Start();
        primary.Missing.Add("a@x");

        await using var fill = FakeNntpServer.Start();
        fill.Articles["a@x"] = YEncTestEncoder.EncodeSinglePart("x.bin", new byte[] { 7, 7, 7 });

        await using var primaryPool = new NntpConnectionPool(ServerAt(primary.Port, "p", NntpServerTier.Primary));
        await using var fillPool = new NntpConnectionPool(ServerAt(fill.Port, "f", NntpServerTier.Fill));
        var fetcher = new MultiServerArticleFetcher([primaryPool, fillPool]);

        var raw = await fetcher.FetchAsync("a@x", articleDate: null, CancellationToken.None);
        var decoded = YEncDecoder.Decode(raw);
        Assert.Equal(new byte[] { 7, 7, 7 }, decoded.Payload);

        // Primary was tried first; Fill was the success.
        Assert.Contains(primary.ReceivedCommands, c => c.StartsWith("BODY", StringComparison.Ordinal));
        Assert.Contains(fill.ReceivedCommands,    c => c.StartsWith("BODY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Throws_ArticleMissingOnAllServers_when_every_server_returns_430()
    {
        await using var a = FakeNntpServer.Start();
        a.Missing.Add("gone@x");
        await using var b = FakeNntpServer.Start();
        b.Missing.Add("gone@x");

        await using var pa = new NntpConnectionPool(ServerAt(a.Port, "a", NntpServerTier.Primary));
        await using var pb = new NntpConnectionPool(ServerAt(b.Port, "b", NntpServerTier.Fill));
        var fetcher = new MultiServerArticleFetcher([pa, pb]);

        var ex = await Assert.ThrowsAsync<ArticleMissingOnAllServersException>(
            () => fetcher.FetchAsync("gone@x", articleDate: null, CancellationToken.None));
        Assert.Equal("gone@x", ex.MessageId);
        Assert.Equal(2, ex.ServerCount);
    }

    [Fact]
    public async Task Respects_priority_within_a_tier()
    {
        // Two Primary servers: priority 0 has the article, priority 10
        // would 430. The priority-0 server must be hit first.
        await using var first = FakeNntpServer.Start();
        first.Articles["a@x"] = YEncTestEncoder.EncodeSinglePart("x.bin", new byte[] { 1 });

        await using var second = FakeNntpServer.Start();
        second.Missing.Add("a@x");

        await using var firstPool = new NntpConnectionPool(ServerAt(first.Port, "first", priority: 0));
        await using var secondPool = new NntpConnectionPool(ServerAt(second.Port, "second", priority: 10));
        var fetcher = new MultiServerArticleFetcher([secondPool, firstPool]); // intentionally registered out of order

        await fetcher.FetchAsync("a@x", articleDate: null, CancellationToken.None);

        // first (priority 0) got the BODY; second (priority 10) was never asked.
        Assert.Contains(first.ReceivedCommands, c => c.StartsWith("BODY", StringComparison.Ordinal));
        Assert.DoesNotContain(second.ReceivedCommands, c => c.StartsWith("BODY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Skips_servers_whose_retention_excludes_old_articles()
    {
        await using var young = FakeNntpServer.Start();    // retention = 7 days; article is 30 days old → skip
        await using var old = FakeNntpServer.Start();      // no retention cap → serve
        old.Articles["old@x"] = YEncTestEncoder.EncodeSinglePart("x.bin", new byte[] { 9 });
        young.Articles["old@x"] = YEncTestEncoder.EncodeSinglePart("x.bin", new byte[] { 9 });

        await using var youngPool = new NntpConnectionPool(
            new NntpServerOptions("young", "young", "127.0.0.1", young.Port, false, RetentionDays: 7, Tier: NntpServerTier.Primary));
        await using var oldPool = new NntpConnectionPool(
            new NntpServerOptions("old", "old", "127.0.0.1", old.Port, false, RetentionDays: null, Tier: NntpServerTier.Backup));
        var fetcher = new MultiServerArticleFetcher([youngPool, oldPool]);

        var articleDate = DateTimeOffset.UtcNow.AddDays(-30);
        await fetcher.FetchAsync("old@x", articleDate, CancellationToken.None);

        // young (7-day retention) was skipped because article is 30 days old.
        Assert.DoesNotContain(young.ReceivedCommands, c => c.StartsWith("BODY", StringComparison.Ordinal));
        Assert.Contains(oldPool.Options.Id, "old"); // sanity
    }
}
