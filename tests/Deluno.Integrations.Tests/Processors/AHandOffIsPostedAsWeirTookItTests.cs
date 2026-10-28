using System.Text.Json.Nodes;
using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Processors;

/// <summary>
/// #1295: the body Deluno posts for a hand-off, against the bodies Weir 1.0.0-rc.18 really took (tests/fixtures/recorded/weir): a hand-off built from
/// the values of a recorded request must be posted as that same body, key for key and in the same order, and Weir's recorded reply is what answers it.
/// A movie, a movie in a release folder and a movie with the files the processor is asked to take.
/// </summary>
public sealed class AHandOffIsPostedAsWeirTookItTests
{
    private static readonly ProcessorConnectionItem Weir = new(
        "connection-1", "Weir", "generic-webhook", "http://weir:8788/api/v1/intake/webhook/deluno",
        "X-Webhook-Secret", "shared-secret", true, "healthy", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Titles(ProcessorHandoffTitle title) : IProcessorHandoffTitleResolver
    {
        public Task<ProcessorHandoffTitle?> ResolveAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken) => Task.FromResult<ProcessorHandoffTitle?>(title);
    }

    [Theory]
    [InlineData("handoff-accepted")]
    [InlineData("handoff-release-folder-accepted")]
    [InlineData("handoff-source-files-accepted")]
    public async Task A_hand_off_built_from_a_recorded_request_is_posted_as_that_body_and_taken_as_Weir_took_it(string state)
    {
        var recording = RecordedReplies.Weir(state);
        var post = recording.Exchanges.First(exchange => exchange.Request.Method == "POST");
        var recorded = JsonNode.Parse(post.Request.Body!)!.AsObject();
        var files = recorded["sourceFiles"]?.AsArray().Select(file => file!.GetValue<string>()).ToArray();
        var handoff = new ProcessorHandoffItem(
            recorded["handoffId"]!.GetValue<string>(), recorded["libraryId"]!.GetValue<string>(), recorded["mediaType"]!.GetValue<string>(), "client-1",
            recorded["queueItemId"]!.GetValue<string>(), recorded["releaseName"]!.GetValue<string>(), recorded["sourcePath"]!.GetValue<string>(),
            "Weir", "waiting", null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SourceFiles: files);
        var title = new ProcessorHandoffTitle(recorded["title"]!.GetValue<string>(), recorded["year"]!.GetValue<int>());
        string? sent = null;
        var handler = RecordedReplies.Handler("weir", [recording], (request, body) =>
        {
            if (request.Method == HttpMethod.Post) sent ??= body;
            return null;
        });

        var result = await new ProcessorConnectionService(new Factory(handler), new Titles(title)).SubmitAsync(Weir, handoff, CancellationToken.None);

        Assert.InRange(post.Reply.Status, 200, 299);
        Assert.True(result.IsAccepted, result.Message);
        Assert.NotNull(sent);
        Assert.Equal(recorded.Select(pair => pair.Key), JsonNode.Parse(sent!)!.AsObject().Select(pair => pair.Key));
        Assert.True(JsonNode.DeepEquals(recorded, JsonNode.Parse(sent!)), $"posted {sent}");
    }
}
