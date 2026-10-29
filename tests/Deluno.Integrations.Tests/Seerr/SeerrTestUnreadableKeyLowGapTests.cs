using Deluno.Connections.Contracts;
using Deluno.Integrations.Seerr;
using Deluno.Security;

namespace Deluno.Integrations.Tests.Seerr;

/// <summary>A Seerr test whose saved key cannot be read here says so, without calling Seerr (#1329).</summary>
public sealed class SeerrTestUnreadableKeyLowGapTests
{
    [Fact]
    public async Task A_Seerr_test_with_an_unreadable_key_answers_degraded_with_the_plain_reason_and_never_calls_Seerr()
    {
        var calls = new CountingFactory();
        var client = new SeerrClient(calls);
        var now = DateTimeOffset.UnixEpoch;
        var connection = new SeerrConnection("seerr-1", "Lab Seerr", "http://seerr.example:5055", null, true, "unknown", null, null, null, null, null, now, now)
        {
            SecretUnreadable = true
        };

        var result = await client.TestAsync(connection, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("degraded", result.Status);
        Assert.Equal(SecretUnreadable.Message("API key"), result.Message);
        Assert.Equal(0, calls.Created);
    }

    private sealed class CountingFactory : IHttpClientFactory
    {
        public int Created { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Created++;
            return new HttpClient(new HttpClientHandler());
        }
    }
}
