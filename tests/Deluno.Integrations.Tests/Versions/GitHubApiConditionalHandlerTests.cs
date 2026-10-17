using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Deluno.Infrastructure.Http;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Versions;

/// <summary>#1181: GitHub's 60 anonymous calls an hour. Stub handlers only, no real GitHub call.</summary>
public sealed class GitHubApiConditionalHandlerTests
{
    private const string Url = "https://api.github.com/repos/example/project/releases?per_page=100";
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_304_reuses_the_stored_body_after_sending_the_last_ETag()
    {
        var stub = new Script(
            _ => Reply(HttpStatusCode.OK, "[1,2,3]", etag: "\"abc\""),
            request =>
            {
                Assert.Equal("\"abc\"", request.Headers.IfNoneMatch.Single().Tag);
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });
        using var http = Client(stub, new FakeTime(Now));

        Assert.Equal("[1,2,3]", await http.GetStringAsync(Url));
        var second = await http.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("[1,2,3]", await second.Content.ReadAsStringAsync());
        Assert.Equal(2, stub.Calls);
        Assert.Empty(stub.SentIfNoneMatch[0]);
        Assert.Equal(["\"abc\""], stub.SentIfNoneMatch[1]);
    }

    [Fact]
    public async Task A_403_with_none_remaining_is_the_limit_says_when_it_lifts_and_is_not_retried_before_then()
    {
        var time = new FakeTime(Now);
        var reset = Now.AddMinutes(35);
        var stub = new Script(_ =>
        {
            var limited = Reply(HttpStatusCode.Forbidden, "{}");
            limited.Headers.Add("X-RateLimit-Remaining", "0");
            limited.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
            return limited;
        }, _ => Reply(HttpStatusCode.OK, "[]"));
        using var http = Client(stub, time);

        var first = await Assert.ThrowsAsync<GitHubRateLimitException>(() => http.GetStringAsync(Url));
        Assert.Contains("used up until", first.Message, StringComparison.Ordinal);
        Assert.Contains("Deluno will try again then", first.Message, StringComparison.Ordinal);
        Assert.Equal(reset, first.ResetsAt);

        // Any other API address too: the allowance is shared.
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => http.GetStringAsync("https://api.github.com/repos/example/other/tags"));
        Assert.Equal(1, stub.Calls);

        time.Set(reset.AddSeconds(1));
        Assert.Equal("[]", await http.GetStringAsync(Url));
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task GitHubs_real_reply_after_the_allowance_is_spent_is_the_limit_and_names_when_it_lifts()
    {
        // GitHub's own reply once the anonymous allowance was spent (recorded from the real api.github.com, tests/fixtures/recorded/github): it is the
        // limit, it lifts at the reset the reply names, and the sentence a person reads gives that minute in UTC. The end-to-end group `github` answers
        // with this same reply.
        var recorded = RecordedReplies.Github("rate-limit-used-up").Single.Reply;
        var reset = DateTimeOffset.FromUnixTimeSeconds(long.Parse(recorded.Headers["x-ratelimit-reset"]));
        var stub = new Script(_ => recorded.ToResponse());
        using var http = Client(stub, new FakeTime(reset.AddMinutes(-30)));

        Assert.Equal(403, recorded.Status);
        var limit = await Assert.ThrowsAsync<GitHubRateLimitException>(() => http.GetStringAsync(Url));

        Assert.Equal(reset, limit.ResetsAt);
        Assert.Equal(GitHubRateLimitException.MessageFor(reset), limit.Message);
        Assert.Contains(" UTC. Deluno will try again then.", limit.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => http.GetStringAsync(Url));
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task A_429_with_none_remaining_is_the_limit_too()
    {
        var stub = new Script(_ =>
        {
            var limited = Reply((HttpStatusCode)429, "{}");
            limited.Headers.Add("X-RateLimit-Remaining", "0");
            limited.Headers.Add("X-RateLimit-Reset", Now.AddHours(1).ToUnixTimeSeconds().ToString());
            return limited;
        });
        using var http = Client(stub, new FakeTime(Now));

        await Assert.ThrowsAsync<GitHubRateLimitException>(() => http.GetStringAsync(Url));
    }

    [Fact]
    public async Task A_403_that_is_not_the_limit_stays_an_ordinary_failure()
    {
        var stub = new Script(_ => Reply(HttpStatusCode.Forbidden, "{}"));
        using var http = Client(stub, new FakeTime(Now));

        var response = await http.GetAsync(Url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Other_addresses_are_not_touched()
    {
        var stub = new Script(_ => Reply(HttpStatusCode.OK, "x", etag: "\"e\""));
        using var http = Client(stub, new FakeTime(Now));

        await http.GetStringAsync("https://github.com/example/project/releases.atom");
        await http.GetStringAsync("https://github.com/example/project/releases.atom");

        Assert.All(stub.SentIfNoneMatch, sent => Assert.Empty(sent));
    }

    private static HttpClient Client(HttpMessageHandler inner, TimeProvider time)
        => new(new GitHubApiConditionalHandler(new GitHubApiState(), time) { InnerHandler = inner });

    private static HttpResponseMessage Reply(HttpStatusCode status, string body, string? etag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }

        return response;
    }

    private sealed class Script(params Func<HttpRequestMessage, HttpResponseMessage>[] replies) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        /// <summary>The If-None-Match values each call carried, in order.</summary>
        public List<string[]> SentIfNoneMatch { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentIfNoneMatch.Add(request.Headers.IfNoneMatch.Select(tag => tag.Tag).ToArray());
            var reply = replies[Math.Min(Calls, replies.Length - 1)];
            Calls++;
            return Task.FromResult(reply(request));
        }
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public void Set(DateTimeOffset value) => now = value;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
