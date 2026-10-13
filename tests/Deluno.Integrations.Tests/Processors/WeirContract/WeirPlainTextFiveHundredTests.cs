using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;
using System.Net;
using static Deluno.Integrations.Tests.Processors.WeirContract.WeirContract;

namespace Deluno.Integrations.Tests.Processors.WeirContract;

/// <summary>
/// #1239: the one Weir reply that cannot be recorded honestly. Weir has no deliberate 5xx or 429 on its intake routes; the only 5xx it
/// can give is the web server's plain-text "Internal Server Error" for an exception nothing handled (SQLite still busy after 30 s,
/// for example), which the golden VM cannot be made to produce on purpose. The reply is built here (the check in scripts/check-recorded-replies.mjs does not recognise a Weir reply, so no
/// allow-list entry is needed; if it ever does, list this file there as "deliberate") and listed in tests/fixtures/recorded/MISSING.md. What it pins is Deluno's
/// side: a plain-text 500 is "not now", never "no", so nothing is dropped or given up on it.
/// </summary>
public sealed class WeirPlainTextFiveHundredTests
{
    private static HttpMessageHandler Always500() => new Fixed(() =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("Internal Server Error") };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        return response;
    });

    private static ProcessorConnectionService Service() => new(new Factory(new HttpClient(Always500())), null);

    [Fact]
    public async Task A_status_poll_answered_with_a_plain_text_500_is_unreachable_so_Deluno_asks_again()
    {
        var status = await Service().GetHandoffStatusAsync(Connection, "01a1371c38837e979272e31cd3ce6c3a", CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unreachable, status.Answer);
    }

    [Fact]
    public async Task A_cancel_answered_with_a_plain_text_500_is_unreachable_so_the_hand_off_is_not_given_up_on()
    {
        var result = await Service().CancelHandoffAsync(Connection, "01a1371c38837e979272e31cd3ce6c3a", CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unreachable, result.Answer);
    }

    [Fact]
    public async Task An_outcome_answered_with_a_plain_text_500_stays_owed_and_is_sent_again()
    {
        var result = await Service().ReportOutcomeAsync(
            Connection, "01a1371c38837e979272e31cd3ce6c3a", ProcessorHandoffOutcome.Imported, "/library/x.mkv", null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unreachable, result.Answer);
        Assert.False(result.Released);
    }

    private sealed class Fixed(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply());
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
