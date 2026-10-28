using System.Net.Http.Json;
using System.Net.Sockets;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1385 (scenario suite, rc.30.36, 28 Oct): the first films after install went to Weir while it was short of threads, the
/// send was cancelled by the HTTP timeout ("Hand-off failed: Gpfilmhsuk01 to Weir on ..., could not reach it
/// (TaskCanceledException)"), and the hand-off was left failed until a person pressed Try again, though Weir answered a
/// minute later. A send that gets no answer now waits and goes again after a back-off, with the same idempotency key;
/// only the last try is a problem. A refusal in the processor's own words still fails at once. The processor's replies
/// are Weir's recorded ones (tests/fixtures/recorded/weir).
/// </summary>
public sealed class AHandOffWhoseSendGetsNoAnswerIsSentAgainTests
{
    private const string Release = "Gpfilmhsuk01.2020.1080p.WEB-DL.x264-GOLDEN";

    private sealed class WeirClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>What each send meets, in order; past the end, the last one again.</summary>
    public enum Meets { Timeout, Refused, Answer }

    private sealed class Rig : IAsyncDisposable
    {
        public ApplicationTestHost App { get; set; } = null!;
        public List<string?> Keys { get; } = [];
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(string recordedState, params Meets[] sends)
    {
        var rig = new Rig();
        var handler = RecordedReplies.Handler(
            "weir",
            [RecordedReplies.Weir(recordedState), RecordedReplies.Weir("library-folders")],
            (request, _) =>
            {
                if (request.Method != HttpMethod.Post) return null;
                rig.Keys.Add(request.Headers.TryGetValues("Idempotency-Key", out var key) ? key.Single() : null);
                switch (sends[Math.Min(rig.Keys.Count, sends.Length) - 1])
                {
                    // What HttpClient throws when its own timeout ends the request, as it did on the VM.
                    case Meets.Timeout: throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());
                    case Meets.Refused: throw new HttpRequestException("No connection could be made because the target machine actively refused it.", new SocketException((int)SocketError.ConnectionRefused));
                    default: return null;
                }
            });
        rig.App = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddSingleton<IProcessorConnectionService>(new ProcessorConnectionService(new WeirClients(handler))));
        return rig;
    }

    private sealed record Scene(LibraryItem Library, ProcessorConnectionItem Connection, ProcessorHandoffItem Handoff);

    private static async Task<Scene> SceneAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Completed")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Ready")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(downloads, Release)).FullName;
        await File.WriteAllBytesAsync(Path.Combine(folder, Release + ".mkv"), new byte[64]);
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films",
            mediaType = "movies",
            rootPath = root,
            downloadsPath = downloads,
            importWorkflow = "refine-before-import",
            processorName = "Weir",
            processorOutputPath = output
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = (await response.Content.ReadFromJsonAsync<LibraryItem>())!;
        var connection = await Repository(app).CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Weir", "weir", "http://weir.example/api/v1/intake/webhook/deluno", null, null, true),
            CancellationToken.None);
        var handoff = await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(library.Id, "movies", "qbittorrent", $"hash-{Release}", Release, folder, "Weir", DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        return new Scene(library, connection, handoff);
    }

    private static IProcessorRepository Repository(ApplicationTestHost app) => app.Services.GetRequiredService<IProcessorRepository>();

    /// <summary>One send, as the planner's pass and Try again make it.</summary>
    private static async Task<ProcessorHandoffItem> SendAsync(ApplicationTestHost app, Scene scene)
    {
        var services = app.Services;
        var current = (await Repository(app).GetProcessorHandoffAsync(scene.Handoff.Id, CancellationToken.None))!;
        var (sent, _) = await ProcessorHandoffSubmission.SubmitAsync(
            current, scene.Library, scene.Connection, "Gpfilmhsuk01", null,
            Repository(app),
            services.GetRequiredService<IProcessorConnectionService>(),
            services.GetRequiredService<IActivityFeedRepository>(),
            CancellationToken.None);
        return sent;
    }

    private static async Task<IReadOnlyList<ActivityEventItem>> LinesAsync(ApplicationTestHost app, string handoffId)
        => await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, "processor-handoff", handoffId, CancellationToken.None);

    [Theory]
    [InlineData(Meets.Timeout)]
    [InlineData(Meets.Refused)]
    public async Task A_send_that_gets_no_answer_waits_and_the_next_send_reaches_the_processor_with_the_same_key(Meets first)
    {
        await using var rig = await StartAsync("handoff-accepted", first, Meets.Answer);
        var app = rig.App;
        var scene = await SceneAsync(app);

        var waiting = await SendAsync(app, scene);

        // Waiting to be sent again, saying so, and not something a person is asked about.
        Assert.Equal(ProcessorHandoffStatuses.Waiting, waiting.Status);
        Assert.Equal($"{scene.Connection.Name} did not answer; Deluno will send it again shortly.", waiting.FailureMessage);
        Assert.False(ProcessorHandoffStatuses.NeedsPerson(waiting.Status, waiting.FailureMessage));
        var tried = Assert.Single(await LinesAsync(app, scene.Handoff.Id), line => line.Category == ProcessorUnreachable.TriedCategory);
        Assert.Contains("again in 1 min", tried.Message);
        Assert.DoesNotContain(await LinesAsync(app, scene.Handoff.Id), line => line.Category == "processing.submission-failed");
        // Not due yet: the planner leaves it for its back-off.
        Assert.True(ProcessorUnreachable.RetryNotDue(waiting, 1, DateTimeOffset.UtcNow));
        Assert.False(ProcessorUnreachable.RetryNotDue(waiting, 1, DateTimeOffset.UtcNow.AddMinutes(1).AddSeconds(1)));

        var submitted = await SendAsync(app, scene);

        Assert.Equal(ProcessorHandoffStatuses.Submitted, submitted.Status);
        Assert.Null(submitted.FailureMessage);
        Assert.Equal(2, rig.Keys.Count);
        Assert.Equal(rig.Keys[0], rig.Keys[1]);
        Assert.Single(await LinesAsync(app, scene.Handoff.Id), line => line.Category == "processing.submitted");
    }

    [Fact]
    public async Task A_send_that_never_gets_an_answer_is_one_problem_after_the_last_try()
    {
        await using var rig = await StartAsync("handoff-accepted", Meets.Timeout);
        var app = rig.App;
        var scene = await SceneAsync(app);

        for (var attempt = 1; attempt <= ProcessorUnreachable.RetryAfter.Count; attempt++)
        {
            Assert.Equal(ProcessorHandoffStatuses.Waiting, (await SendAsync(app, scene)).Status);
        }

        var failed = await SendAsync(app, scene);

        Assert.Equal(ProcessorHandoffStatuses.Failed, failed.Status);
        Assert.Equal($"Deluno could not reach {scene.Connection.Name} after 4 tries. Check that {scene.Connection.Name} is running and its connection in Deluno is right, then send it again.", failed.FailureMessage);
        Assert.True(ProcessorHandoffStatuses.NeedsPerson(failed.Status, failed.FailureMessage));
        var lines = await LinesAsync(app, scene.Handoff.Id);
        Assert.Equal(ProcessorUnreachable.RetryAfter.Count, lines.Count(line => line.Category == ProcessorUnreachable.TriedCategory));
        Assert.Single(lines, line => line.Category == "processing.submission-failed");
        Assert.Equal(4, rig.Keys.Count);
        Assert.Single(rig.Keys.Distinct());

        // A person sends it again: the count starts afresh, so it backs off again rather than failing at once.
        await Repository(app).UpdateProcessorHandoffAsync(scene.Handoff.Id, ProcessorHandoffStatuses.Waiting, null, null, null, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Waiting, (await SendAsync(app, scene)).Status);
    }

    [Fact]
    public async Task A_refusal_in_the_processors_own_words_fails_at_once_and_is_not_sent_again()
    {
        await using var rig = await StartAsync("handoff-refused-not-watched", Meets.Answer);
        var app = rig.App;
        var scene = await SceneAsync(app);

        var refused = await SendAsync(app, scene);

        Assert.Equal(ProcessorHandoffStatuses.Failed, refused.Status);
        Assert.StartsWith("The processor refused the hand-off with HTTP", refused.FailureMessage);
        Assert.False(ProcessorUnreachable.IsWaitingMessage(refused.FailureMessage));
        var lines = await LinesAsync(app, scene.Handoff.Id);
        Assert.DoesNotContain(lines, line => line.Category == ProcessorUnreachable.TriedCategory);
        Assert.Single(lines, line => line.Category == "processing.submission-failed");
    }
}
