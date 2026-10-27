using System.Net;
using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;
using static Deluno.Integrations.Tests.Processors.WeirContract.WeirContract;

namespace Deluno.Integrations.Tests.Processors.WeirContract;

/// <summary>
/// #1301: a reply that is not Weir's. A web page where Weir's JSON should be (a proxy in front of Weir answering for it), Weir's
/// own recorded reply cut off half way (malformed JSON) and a gateway error each come back from the capabilities, status and
/// library-folders asks. Deluno never takes one for an answer: nothing is assumed Weir can do, a status it cannot read is
/// "unreachable" (so it asks again) and no folders are listed. The cut-off bodies are Weir's recordings, shortened.
/// </summary>
public sealed class WeirUnexpectedReplyTests
{
    private const string GatewayPage = "<html><body><h1>502 Bad Gateway</h1></body></html>";

    private sealed class Answer(HttpStatusCode status, string body, string type) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, type) });
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static ProcessorConnectionService Service(HttpStatusCode status, string body, string type)
        => new(new Factory(new HttpClient(new Answer(status, body, type))));

    private static string Half(string text) => text[..(text.Length / 2)];

    [Fact]
    public async Task A_web_page_or_a_cut_off_reply_to_the_capabilities_ask_assumes_nothing_Weir_can_do()
    {
        var capabilities = Rec("capabilities").Single.Reply.Body!;
        foreach (var body in new[] { GatewayPage, Half(capabilities) })
        {
            var support = await Service(HttpStatusCode.OK, body, "application/json").GetStatusSupportAsync(Connection, CancellationToken.None);

            Assert.Equal(ProcessorAnswer.Answered, support.Answer);
            Assert.False(support.CanReportStatus);
            Assert.False(support.CanCancel);
            Assert.False(support.CanBeToldTheOutcome);
            Assert.False(support.HasLibraryFolders);
        }
    }

    [Fact]
    public async Task A_web_page_or_a_cut_off_reply_to_a_status_ask_is_unreachable_so_Deluno_asks_again_and_never_a_status()
    {
        var recorded = Rec("status-working");
        var id = HandoffIdOf(recorded);
        foreach (var body in new[] { GatewayPage, recorded.Exchanges[0].Reply.Body![..40] })
        {
            var status = await Service(HttpStatusCode.OK, body, "application/json").GetHandoffStatusAsync(Connection, id, CancellationToken.None);

            Assert.Equal(ProcessorAnswer.Unreachable, status.Answer);
            Assert.Equal("Weir answered with a status Deluno could not read.", status.Message);
        }
    }

    [Fact]
    public async Task A_web_page_or_a_cut_off_reply_to_the_library_folders_ask_lists_no_folders()
    {
        var folders = Rec("library-folders").Single.Reply.Body!;
        foreach (var body in new[] { GatewayPage, Half(folders) })
        {
            Assert.Empty(await Service(HttpStatusCode.OK, body, "application/json").GetLibraryFoldersAsync(Connection, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_gateway_error_on_capabilities_status_and_library_folders_is_unreachable_with_its_status_named_and_no_folders()
    {
        var id = HandoffIdOf(Rec("status-working"));
        var service = Service(HttpStatusCode.BadGateway, GatewayPage, "text/html");

        var support = await service.GetStatusSupportAsync(Connection, CancellationToken.None);
        var status = await service.GetHandoffStatusAsync(Connection, id, CancellationToken.None);
        var folders = await service.GetLibraryFoldersAsync(Connection, CancellationToken.None);

        Assert.Equal(ProcessorAnswer.Unreachable, support.Answer);
        Assert.Contains("Weir answered HTTP 502", support.Message);
        Assert.Equal(ProcessorAnswer.Unreachable, status.Answer);
        Assert.Contains("Weir answered HTTP 502", status.Message);
        Assert.Empty(folders);
    }
}
