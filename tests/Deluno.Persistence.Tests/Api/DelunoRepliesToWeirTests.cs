using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Deluno.Integrations.Processors;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1239: Deluno's own replies to the calls Weir makes of it are pinned. Weir reads four routes of Deluno
/// (<c>GET /api/integrations/processors/handoffs</c>, <c>GET .../download-destinations</c>, <c>POST .../handoffs/{id}/import-original</c>
/// and <c>POST .../handoffs/{id}/outcome/send-again</c>) with its Deluno API key, and builds its own client against what they
/// answer. The answers are recorded from the real server code (the whole application over HTTP, the same two composition calls
/// the shipped hosts make) in tests/fixtures/recorded/deluno/&lt;version&gt;/reply-*, and each test here runs the same calls again
/// and holds Deluno's live reply to its recording member by member: the status, the headers that matter, and every member of the
/// body in its order, with its type and its words. A reply that changes shape fails here, before Weir meets it.
///
/// <para><b>What is changed in a recording.</b> Only what no run can repeat: a generated id becomes <c>&lt;id&gt;</c> (a hand-off
/// or library the scenario made is <c>&lt;handoff:label&gt;</c> or <c>&lt;library:label&gt;</c>), a time becomes <c>&lt;utc&gt;</c>,
/// the test's data folder becomes <c>&lt;data-root&gt;</c> (with forward slashes) and the key becomes a name. Everything else is
/// what Deluno said.</para>
///
/// <para><b>How a recording is made.</b> Run these tests with the environment variable <c>DELUNO_RECORD_REPLIES=1</c> and each
/// scenario writes its file, under the version in <see cref="RecordedVersion"/>; run them without it and each is replayed. A
/// reply Weir's stand-in gives (the outcome Weir answers a "send again" with) is a recording of the real Weir, never written
/// here.</para>
/// </summary>
public sealed partial class DelunoRepliesToWeirTests
{
    /// <summary>The Deluno version these recordings were made at: the folder under tests/fixtures/recorded/deluno.</summary>
    public const string RecordedVersion = "1.0.0-rc.30.32";

    private const string Weir = "Weir";
    private const string WeirUrl = "http://weir.example/api/v1/intake/webhook/deluno";

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Deluno_still_answers_what_it_was_recorded_answering(string state)
    {
        var scenario = Scenarios()[state];
        await using var rig = await Rig.StartAsync(scenario);
        await scenario.Run(rig);

        var live = rig.Calls;
        if (Environment.GetEnvironmentVariable("DELUNO_RECORD_REPLIES") == "1")
        {
            Write(scenario, live);
            return;
        }

        var recorded = RecordedReplies.Load("deluno", RecordedVersion, state);
        Assert.Equal(recorded.Exchanges.Count, live.Count);
        var differences = new List<string>();
        for (var index = 0; index < live.Count; index++)
        {
            differences.AddRange(Differences(index, recorded.Exchanges[index], live[index]));
        }

        Assert.True(differences.Count == 0, $"{state} no longer matches its recording:{Environment.NewLine}" + string.Join(Environment.NewLine, differences));
    }

    [Fact]
    public void Every_scenario_has_its_recording_and_every_reply_recording_has_its_scenario()
    {
        var scenarios = Scenarios().Keys.ToHashSet();
        var recorded = RecordedReplies.States("deluno", RecordedVersion).Where(state => state.StartsWith("reply-", StringComparison.Ordinal)).ToHashSet();
        Assert.True(scenarios.SetEquals(recorded),
            "Scenarios without a recording: " + string.Join(", ", scenarios.Except(recorded))
            + "; recordings without a scenario: " + string.Join(", ", recorded.Except(scenarios))
            + ". Record with DELUNO_RECORD_REPLIES=1.");
    }

    [Fact]
    public void Every_route_Weir_reads_is_recorded_for_each_of_its_reply_states()
    {
        // The states each route can answer; a route or a state that leaves this list needs a recording of its own first.
        var all = RecordedReplies.States("deluno", RecordedVersion).Where(state => state.StartsWith("reply-", StringComparison.Ordinal)).ToArray();
        string[] required =
        [
            "reply-handoffs-empty", "reply-handoffs-listed", "reply-handoffs-with-outcomes",
            "reply-download-destinations-empty", "reply-download-destinations-libraries",
            "reply-import-original-accepted", "reply-import-original-not-yet", "reply-import-original-unknown",
            "reply-send-again-nothing-recorded", "reply-send-again-delivered", "reply-send-again-connection-removed",
            "reply-no-sign-in", "reply-key-without-imports-scope", "reply-key-with-imports-scope-only"
        ];
        Assert.Empty(required.Except(all));
    }

    public static IEnumerable<object[]> Names() => Scenarios().Keys.Select(state => new object[] { state });

    // ---- the scenarios -------------------------------------------------------------------------------------------------------

    private sealed record Scenario(string State, string Note, Func<Rig, Task> Run, Func<HttpMessageHandler>? WeirStandIn = null);

    private static IReadOnlyDictionary<string, Scenario> Scenarios() => All.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, Scenario>> All = new(() =>
    {
        var list = new List<Scenario>
        {
            new("reply-handoffs-empty",
                "Weir lists Deluno's hand-offs on a Deluno that has made none, for every library and for one.",
                async rig =>
                {
                    var scene = await rig.LibraryAsync("films");
                    await rig.GetAsync("/api/integrations/processors/handoffs");
                    await rig.GetAsync($"/api/integrations/processors/handoffs?libraryId={scene.LibraryId}");
                }),

            new("reply-handoffs-listed",
                "Weir lists Deluno's hand-offs: one waiting, one the processor failed and one that completed, for every library, for one library, for a library with none, and with a limit of one.",
                async rig =>
                {
                    var films = await rig.LibraryAsync("films");
                    var shows = await rig.LibraryAsync("shows", mediaType: "tv");
                    await rig.HandoffAsync(films, "waiting");
                    await rig.HandoffAsync(films, "failed", ProcessorHandoffStatuses.Failed, "Weir could not finish it.");
                    await rig.HandoffAsync(films, "completed", ProcessorHandoffStatuses.Completed);
                    await rig.GetAsync("/api/integrations/processors/handoffs");
                    await rig.GetAsync($"/api/integrations/processors/handoffs?libraryId={films.LibraryId}");
                    await rig.GetAsync($"/api/integrations/processors/handoffs?libraryId={shows.LibraryId}");
                    await rig.GetAsync("/api/integrations/processors/handoffs?take=1");
                }),

            new("reply-handoffs-with-outcomes",
                "Weir lists hand-offs that each carry the outcome Deluno owes or told it: pending, delivered, refused, given up, never received, settled and replaced.",
                async rig =>
                {
                    var films = await rig.LibraryAsync("films");
                    foreach (var (label, delivery, released, said) in new[]
                    {
                        ("pending", ProcessorOutcomeDeliveries.Pending, false, "Deluno could not reach Weir."),
                        ("delivered", ProcessorOutcomeDeliveries.Delivered, true, "Weir recorded that Deluno imported the file. It released its copy."),
                        ("refused", ProcessorOutcomeDeliveries.Refused, false, "Weir refused the report."),
                        ("given-up", ProcessorOutcomeDeliveries.GivenUp, false, "Nobody acknowledged it in 7 days, so Deluno stopped trying."),
                        ("never-received", ProcessorOutcomeDeliveries.NeverReceived, false, "Weir has never received this hand-off."),
                        ("settled", ProcessorOutcomeDeliveries.Settled, false, "Weir kept its earlier answer."),
                        ("replaced", ProcessorOutcomeDeliveries.Replaced, false, "The release was sent again after this send.")
                    })
                    {
                        var handoff = await rig.HandoffAsync(films, label, ProcessorHandoffStatuses.Completed);
                        await rig.OutcomeAsync(handoff, delivery, released, said);
                    }

                    await rig.GetAsync("/api/integrations/processors/handoffs");
                }),

            new("reply-download-destinations-empty",
                "Weir asks where downloads land on a Deluno with no library: an empty list, and a library id Deluno does not know.",
                async rig =>
                {
                    await rig.GetAsync("/api/integrations/processors/download-destinations");
                    await rig.GetAsync("/api/integrations/processors/download-destinations?libraryId=no-such-library");
                }),

            new("reply-download-destinations-libraries",
                "Weir asks where downloads land: a movie library that hands to Weir with its path mapping, a TV library with no processor, for every library, for one, and for a library Deluno does not know.",
                async rig =>
                {
                    var films = await rig.LibraryAsync("films", withConnection: true);
                    var shows = await rig.LibraryAsync("shows", mediaType: "tv", processor: false);
                    await rig.PathMappingAsync(films, "/downloads", "D:/Downloads");
                    await rig.GetAsync("/api/integrations/processors/download-destinations");
                    await rig.GetAsync($"/api/integrations/processors/download-destinations?libraryId={films.LibraryId}");
                    await rig.GetAsync($"/api/integrations/processors/download-destinations?libraryId={shows.LibraryId}");
                    await rig.GetAsync("/api/integrations/processors/download-destinations?libraryId=no-such-library");
                }),

            new("reply-import-original-accepted",
                "Weir (or a person) asks Deluno to import the original of a hand-off the processor did not finish: failed, timed out, stalled, lost and cancelled are each accepted with the hand-off as it now stands.",
                async rig =>
                {
                    var films = await rig.LibraryAsync("films");
                    foreach (var status in new[] { ProcessorHandoffStatuses.Failed, ProcessorHandoffStatuses.TimedOut, ProcessorHandoffStatuses.Stalled, ProcessorHandoffStatuses.Lost, ProcessorHandoffStatuses.Cancelled })
                    {
                        var handoff = await rig.HandoffAsync(films, status, status, "It did not finish.");
                        await rig.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/import-original");
                    }
                }),

            new("reply-import-original-not-yet",
                "Deluno refuses to import the original while the processor may still deliver, or when there is nothing to import: waiting, submitted, accepted, started, completed, rejected, released and dismissed each answer 409 with the reason.",
                async rig =>
                {
                    var films = await rig.LibraryAsync("films");
                    foreach (var status in new[]
                    {
                        ProcessorHandoffStatuses.Waiting, ProcessorHandoffStatuses.Submitted, ProcessorHandoffStatuses.Accepted, ProcessorHandoffStatuses.Started,
                        ProcessorHandoffStatuses.Completed, ProcessorHandoffStatuses.Rejected, ProcessorHandoffStatuses.Released, ProcessorHandoffStatuses.Dismissed
                    })
                    {
                        var handoff = await rig.HandoffAsync(films, status, status == ProcessorHandoffStatuses.Waiting ? null : status, null);
                        await rig.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/import-original");
                    }
                }),

            new("reply-import-original-unknown",
                "Weir asks to import the original of a hand-off Deluno has no record of: 404.",
                async rig => await rig.PostAsync("/api/integrations/processors/handoffs/no-such-handoff/import-original")),

            new("reply-send-again-nothing-recorded",
                "Weir asks Deluno to tell it again what became of a hand-off, and Deluno has recorded no outcome for it (none is made until an import finishes): 404 with the reason.",
                async rig =>
                {
                    var films = await rig.LibraryAsync("films");
                    var handoff = await rig.HandoffAsync(films, "waiting");
                    await rig.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/outcome/send-again");
                    await rig.PostAsync("/api/integrations/processors/handoffs/no-such-handoff/outcome/send-again");
                }),

            new("reply-send-again-connection-removed",
                "Deluno is asked to say an outcome again and the processor connection is gone: 200, the outcome given up, and the words that there is nobody left to tell.",
                async rig =>
                {
                    var films = await rig.LibraryAsync("films");
                    var handoff = await rig.HandoffAsync(films, "imported", ProcessorHandoffStatuses.Completed);
                    await rig.OutcomeAsync(handoff, ProcessorOutcomeDeliveries.Refused, false, "Weir refused the report.");
                    await rig.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/outcome/send-again");
                }),

            new("reply-no-sign-in",
                "Each of the four routes called with no credential at all.",
                async rig => await rig.EveryRouteAsync(Credential.None)),

            new("reply-key-without-imports-scope",
                "Each of the four routes called with a valid API key that carries only the read scope: the two reads reach the route and are refused by it, the two writes are refused before it.",
                async rig => await rig.EveryRouteAsync(Credential.ReadOnlyKey)),

            new("reply-key-with-imports-scope-only",
                "Each of the four routes called with a valid API key that carries the imports scope and no read scope: the two reads answer 200 (a GET asks for read, except these two hand-off reads, which the imports scope also opens, #1265) and the two writes are accepted past the scope check.",
                async rig => await rig.EveryRouteAsync(Credential.ImportsOnly))
        };

        // Deluno tells Weir again what became of a file, and Weir (the real one, as recorded) answers each way it can.
        list.Add(SendAgain("reply-send-again-delivered", "Weir acknowledges the outcome and keeps its copy (200, released false).",
            () => WeirAnswering("capabilities", "outcome-delivered")));
        list.Add(SendAgain("reply-send-again-delivered-released", "Weir acknowledges the outcome and lets go of its copy (200, released true).",
            () => WeirAnswering("capabilities", "outcome-finished-in-burst-200")));
        list.Add(SendAgain("reply-send-again-weir-has-its-answer", "Weir says it already holds an answer (409 outcome_already_recorded): settled.",
            () => WeirAnswering("capabilities", "outcome-outcome-already-recorded")));
        list.Add(SendAgain("reply-send-again-weir-hand-off-ended", "Weir says the hand-off ended without a file (409 handoff_ended): refused for good.",
            () => WeirAnswering("capabilities", "outcome-handoff-ended")));
        list.Add(SendAgain("reply-send-again-weir-not-finished", "Weir says it has not finished the hand-off (409 handoff_not_finished): still owed, tried again later.",
            () => WeirAnswering("capabilities", "outcome-handoff-not-finished-working")));
        list.Add(SendAgain("reply-send-again-weir-never-received", "Weir says it never received the hand-off (404): nothing to tell.",
            () => WeirAnswering("capabilities", "outcome-unknown-id")));
        list.Add(SendAgain("reply-send-again-weir-refuses-secret", "Weir refuses the secret on its first question (401): still owed.",
            () => WeirAnswering("capabilities-wrong-secret")));
        list.Add(SendAgain("reply-send-again-weir-unreachable", "Weir cannot be reached (connection refused): still owed, tried again later.",
            () => new Unreachable()));

        return list.ToDictionary(scenario => scenario.State);
    });

    private static Scenario SendAgain(string state, string note, Func<HttpMessageHandler> weir)
        => new(state, "Deluno is asked to say an outcome again. " + note,
            async rig =>
            {
                var films = await rig.LibraryAsync("films", withConnection: true);
                var handoff = await rig.HandoffAsync(films, "imported", ProcessorHandoffStatuses.Completed);
                await rig.OutcomeAsync(handoff, ProcessorOutcomeDeliveries.Pending, false, "Deluno could not reach Weir.");
                await rig.PostAsync($"/api/integrations/processors/handoffs/{handoff.Id}/outcome/send-again");
            },
            weir);

    private static HttpMessageHandler WeirAnswering(params string[] states)
        => new WeirRecorded(states.Select(RecordedReplies.Weir).ToArray());

    /// <summary>
    /// The real Weir, as recorded: each call gets the recorded reply to the same kind of call. A hand-off is named in the address by
    /// its id, which a recording holds as Weir wrote it and a scenario makes for itself, so the id is left out of the match.
    /// </summary>
    private sealed class WeirRecorded(Recording[] recordings) : HttpMessageHandler
    {
        private static string Kind(string path) => Regex.Replace(path, "/deluno/[^/?]+", "/deluno/{id}");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var kind = Kind(request.RequestUri!.PathAndQuery);
            foreach (var exchange in recordings.SelectMany(recording => recording.Exchanges))
            {
                if (exchange.Request.Method.Equals(request.Method.Method, StringComparison.OrdinalIgnoreCase) && Kind(exchange.Request.Path) == kind)
                {
                    return Task.FromResult(exchange.Reply.ToResponse());
                }
            }

            throw new InvalidOperationException($"{request.Method} {kind} was never recorded for ({string.Join(", ", recordings.Select(recording => recording.State))}).");
        }
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
    }

    private sealed class WeirClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // ---- the rig: a whole Deluno, called the way Weir calls it ---------------------------------------------------------------------

    /// <summary>Imports: the key Weir is given (read and imports). ImportsOnly: imports and nothing else. ReadOnlyKey: read and nothing else.</summary>
    private enum Credential { Imports, ImportsOnly, ReadOnlyKey, None }

    private sealed record Scene(string Label, string LibraryId, string Downloads, string Output);

    private sealed record Captured(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> RequestHeaders,
        int Status,
        IReadOnlyDictionary<string, string> ReplyHeaders,
        JsonNode? Body,
        string? BodyText);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly Normalizer _normalizer;
        private readonly HttpClient _imports;
        private readonly HttpClient _importsOnly;
        private readonly HttpClient _readOnly;
        private readonly HttpClient _anonymous;

        private Rig(ApplicationTestHost app, Normalizer normalizer, HttpClient imports, HttpClient importsOnly, HttpClient readOnly, HttpClient anonymous)
        {
            App = app;
            _normalizer = normalizer;
            _imports = imports;
            _importsOnly = importsOnly;
            _readOnly = readOnly;
            _anonymous = anonymous;
        }

        public ApplicationTestHost App { get; }

        public List<Captured> Calls { get; } = [];

        public static async Task<Rig> StartAsync(Scenario scenario)
        {
            var app = await ApplicationTestHost.StartAsync(replaceServices: scenario.WeirStandIn is null
                ? null
                : services => services.AddSingleton<IProcessorConnectionService>(new ProcessorConnectionService(new WeirClients(scenario.WeirStandIn()))));
            var normalizer = new Normalizer(app.DataRoot);
            return new Rig(
                app,
                normalizer,
                await KeyClientAsync(app, "Weir", "read,imports"),
                await KeyClientAsync(app, "Imports only", "imports"),
                await KeyClientAsync(app, "Read only", "read"),
                app.CreateAnonymousClient());
        }

        private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string name, string scopes)
        {
            var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name, scopes });
            created.EnsureSuccessStatusCode();
            var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
            var client = app.CreateAnonymousClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
            return client;
        }

        public ValueTask DisposeAsync()
        {
            _imports.Dispose();
            _importsOnly.Dispose();
            _readOnly.Dispose();
            _anonymous.Dispose();
            return App.DisposeAsync();
        }

        public Task<Captured> GetAsync(string path, Credential credential = Credential.Imports) => SendAsync(HttpMethod.Get, path, credential);

        public Task<Captured> PostAsync(string path, Credential credential = Credential.Imports) => SendAsync(HttpMethod.Post, path, credential);

        public async Task EveryRouteAsync(Credential credential)
        {
            await GetAsync("/api/integrations/processors/handoffs", credential);
            await GetAsync("/api/integrations/processors/download-destinations", credential);
            await PostAsync("/api/integrations/processors/handoffs/no-such-handoff/import-original", credential);
            await PostAsync("/api/integrations/processors/handoffs/no-such-handoff/outcome/send-again", credential);
        }

        private async Task<Captured> SendAsync(HttpMethod method, string path, Credential credential)
        {
            var client = credential switch { Credential.Imports => _imports, Credential.ImportsOnly => _importsOnly, Credential.ReadOnlyKey => _readOnly, _ => _anonymous };
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            var requestHeaders = new Dictionary<string, string>();
            if (credential != Credential.None)
            {
                requestHeaders["x-api-key"] = credential switch
                {
                    Credential.Imports => "<api key with the read and imports scopes>",
                    Credential.ImportsOnly => "<api key with the imports scope and no read scope>",
                    _ => "<api key with the read scope and no imports scope>"
                };
            }

            var replyHeaders = new Dictionary<string, string>();
            if (response.Content.Headers.ContentType is { } contentType)
            {
                replyHeaders["content-type"] = contentType.ToString();
            }

            if (response.Headers.Location is { } location)
            {
                replyHeaders["location"] = _normalizer.Text(location.OriginalString);
            }

            JsonNode? body = null;
            string? bodyText = null;
            if (text.Length > 0)
            {
                if (replyHeaders.TryGetValue("content-type", out var type) && type.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    body = _normalizer.Node(JsonNode.Parse(text));
                }
                else
                {
                    bodyText = _normalizer.Text(text);
                }
            }

            var captured = new Captured(method.Method, _normalizer.Text(path), requestHeaders, (int)response.StatusCode, replyHeaders, body, bodyText);
            Calls.Add(captured);
            return captured;
        }

        public async Task<Scene> LibraryAsync(string label, string mediaType = "movies", bool processor = true, bool withConnection = false)
        {
            var root = Directory.CreateDirectory(Path.Combine(App.DataRoot, label)).FullName;
            var downloads = Directory.CreateDirectory(Path.Combine(App.DataRoot, label + "-downloads")).FullName;
            var output = Directory.CreateDirectory(Path.Combine(App.DataRoot, label + "-ready")).FullName;
            object payload = processor
                ? new
                {
                    name = label,
                    mediaType,
                    rootPath = root,
                    downloadsPath = downloads,
                    importWorkflow = "refine-before-import",
                    processorName = (string?)Weir,
                    processorOutputPath = (string?)output
                }
                : new
                {
                    name = label,
                    mediaType,
                    rootPath = root,
                    downloadsPath = downloads,
                    importWorkflow = "standard",
                    processorName = (string?)null,
                    processorOutputPath = (string?)null
                };
            var response = await App.Client.PostAsJsonAsync("/api/libraries", payload);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var library = await response.Content.ReadFromJsonAsync<LibraryItem>();
            _normalizer.Alias(library!.Id, $"<library:{label}>");
            if (withConnection)
            {
                var created = await Processors.CreateProcessorConnectionAsync(
                    new CreateProcessorConnectionRequest(Weir, "weir", WeirUrl, null, null, true),
                    CancellationToken.None);
                await LegacyConnectionNames.SetAsync(
                    App.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>(), "processor_connections", created.Id, Weir);
                _normalizer.Alias(created.Id, "<connection:weir>");
                Connection = created.Id;
            }

            return new Scene(label, library.Id, downloads, output);
        }

        public string? Connection { get; private set; }

        public async Task PathMappingAsync(Scene scene, string delunoPath, string processorPath)
        {
            var response = await App.Client.PostAsJsonAsync(
                $"/api/integrations/processors/connections/{Connection}/path-mappings",
                new { delunoPath, processorPath, isEnabled = true, priority = 1 });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        private IProcessorRepository Processors => App.Services.GetRequiredService<IProcessorRepository>();

        public async Task<ProcessorHandoffItem> HandoffAsync(Scene scene, string label, string? status = null, string? message = null)
        {
            var release = $"Example.Film.{label}.2020.1080p.WEB-DL.x264-GROUP";
            var handoff = await Processors.EnsureProcessorHandoffAsync(
                new CreateProcessorHandoffRequest(scene.LibraryId, scene.Label == "shows" ? "tv" : "movies", "qbittorrent", $"queue-{label}", release, Path.Combine(scene.Downloads, release), Weir),
                CancellationToken.None);
            _normalizer.Alias(handoff.Id, $"<handoff:{label}>");
            if (status is not null && status != ProcessorHandoffStatuses.Waiting)
            {
                handoff = (await Processors.UpdateProcessorHandoffAsync(handoff.Id, status, null, null, message, CancellationToken.None))!;
            }

            await Wait.ClockMovesOnAsync();
            return handoff;
        }

        public Task OutcomeAsync(ProcessorHandoffItem handoff, string delivery, bool released, string said)
        {
            var at = DateTimeOffset.Parse("2026-10-14T09:00:00Z");
            return Processors.SaveProcessorOutcomeAsync(
                new ProcessorOutcomeRecord(
                    handoff.Id, Weir, handoff.LibraryId, handoff.ReleaseName, ProcessorOutcomeRecord.Imported,
                    Path.Combine(App.DataRoot, "films", handoff.ReleaseName + ".mkv"), null,
                    at, delivery, 1, at, delivery == ProcessorOutcomeDeliveries.Pending ? at.AddMinutes(1) : null, released, said, at),
                CancellationToken.None);
        }
    }

    // ---- normalising what no run can repeat ----------------------------------------------------------------------------------------

    private sealed partial class Normalizer(string dataRoot)
    {
        private readonly List<(string Value, string Token)> _aliases = [];

        public void Alias(string value, string token) => _aliases.Add((value, token));

        public string Text(string text)
        {
            foreach (var (value, token) in _aliases)
            {
                text = text.Replace(value, token, StringComparison.Ordinal);
            }

            var rooted = false;
            foreach (var variant in new[] { dataRoot, dataRoot.Replace('\\', '/'), dataRoot.Replace("\\", "\\\\") }.Distinct())
            {
                if (text.Contains(variant, StringComparison.Ordinal))
                {
                    text = text.Replace(variant, "<data-root>", StringComparison.Ordinal);
                    rooted = true;
                }
            }

            if (rooted)
            {
                text = text.Replace('\\', '/');
            }

            text = Time().Replace(text, "<utc>");
            return Id().Replace(text, "<id>");
        }

        public JsonNode? Node(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    var copy = new JsonObject();
                    foreach (var (name, value) in obj)
                    {
                        copy[name] = Node(value);
                    }

                    return copy;
                case JsonArray array:
                    return new JsonArray(array.Select(item => Node(item)).ToArray());
                case JsonValue value when value.TryGetValue<string>(out var text):
                    return JsonValue.Create(Text(text));
                default:
                    return node?.DeepClone();
            }
        }

        [GeneratedRegex(@"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)")]
        private static partial Regex Time();

        [GeneratedRegex(@"(?<![0-9a-zA-Z])[0-9a-f]{32}(?![0-9a-zA-Z])")]
        private static partial Regex Id();
    }

    // ---- comparing a live reply to its recording, member by member -------------------------------------------------------------------

    private static IEnumerable<string> Differences(int index, Exchange recorded, Captured live)
    {
        var at = $"exchange {index + 1} ({live.Method} {live.Path})";
        if (!string.Equals(recorded.Request.Method, live.Method, StringComparison.Ordinal))
        {
            yield return $"{at}: method {live.Method}, recorded {recorded.Request.Method}";
        }

        if (!string.Equals(recorded.Request.Path, live.Path, StringComparison.Ordinal))
        {
            yield return $"{at}: path {live.Path}, recorded {recorded.Request.Path}";
        }

        var recordedRequestHeaders = recorded.Request.Headers ?? new Dictionary<string, string>();
        foreach (var difference in Map(at + " request header", recordedRequestHeaders, live.RequestHeaders))
        {
            yield return difference;
        }

        if (recorded.Reply.Status != live.Status)
        {
            yield return $"{at}: status {live.Status}, recorded {recorded.Reply.Status}";
        }

        foreach (var difference in Map(at + " reply header", recorded.Reply.Headers, live.ReplyHeaders))
        {
            yield return difference;
        }

        if (live.Body is not null)
        {
            if (recorded.Reply.Body is null)
            {
                yield return $"{at}: Deluno now answers a body, recorded none";
            }
            else
            {
                foreach (var difference in Compare(at + " body", JsonNode.Parse(recorded.Reply.Body), live.Body))
                {
                    yield return difference;
                }
            }
        }
        else if (live.BodyText is not null)
        {
            if (!string.Equals(recorded.Reply.Body, live.BodyText, StringComparison.Ordinal))
            {
                yield return $"{at}: text '{live.BodyText}', recorded '{recorded.Reply.Body}'";
            }
        }
        else if (!string.IsNullOrEmpty(recorded.Reply.Body))
        {
            yield return $"{at}: Deluno now answers no body, recorded '{recorded.Reply.Body}'";
        }
    }

    private static IEnumerable<string> Map(string what, IReadOnlyDictionary<string, string> recorded, IReadOnlyDictionary<string, string> live)
    {
        foreach (var (name, value) in live)
        {
            if (!recorded.TryGetValue(name, out var was))
            {
                yield return $"{what} '{name}' is new ({value})";
            }
            else if (!string.Equals(was, value, StringComparison.Ordinal))
            {
                yield return $"{what} '{name}': {value}, recorded {was}";
            }
        }

        foreach (var name in recorded.Keys.Where(name => !live.ContainsKey(name)))
        {
            yield return $"{what} '{name}' is gone (recorded {recorded[name]})";
        }
    }

    /// <summary>Member by member: the same names in the same order, the same kind of value, the same words and numbers.</summary>
    private static IEnumerable<string> Compare(string path, JsonNode? recorded, JsonNode? live)
    {
        switch (recorded, live)
        {
            case (null, null):
                yield break;
            case (JsonObject was, JsonObject now):
                var wasNames = was.Select(pair => pair.Key).ToArray();
                var nowNames = now.Select(pair => pair.Key).ToArray();
                if (!wasNames.SequenceEqual(nowNames))
                {
                    yield return $"{path}: members [{string.Join(", ", nowNames)}], recorded [{string.Join(", ", wasNames)}]";
                }

                foreach (var name in wasNames.Intersect(nowNames))
                {
                    foreach (var difference in Compare($"{path}.{name}", was[name], now[name]))
                    {
                        yield return difference;
                    }
                }

                yield break;
            case (JsonArray was, JsonArray now):
                if (was.Count != now.Count)
                {
                    yield return $"{path}: {now.Count} items, recorded {was.Count}";
                }

                for (var item = 0; item < Math.Min(was.Count, now.Count); item++)
                {
                    foreach (var difference in Compare($"{path}[{item}]", was[item], now[item]))
                    {
                        yield return difference;
                    }
                }

                yield break;
            case (JsonValue was, JsonValue now):
                if (was.GetValueKind() != now.GetValueKind())
                {
                    yield return $"{path}: {now.GetValueKind()} {now.ToJsonString()}, recorded {was.GetValueKind()} {was.ToJsonString()}";
                }
                else if (was.ToJsonString() != now.ToJsonString())
                {
                    yield return $"{path}: {now.ToJsonString()}, recorded {was.ToJsonString()}";
                }

                yield break;
            default:
                yield return $"{path}: {(live is null ? "null" : live.GetValueKind().ToString())}, recorded {(recorded is null ? "null" : recorded.GetValueKind().ToString())}";
                yield break;
        }
    }

    // ---- writing a recording (DELUNO_RECORD_REPLIES=1) ---------------------------------------------------------------------------

    private static void Write(Scenario scenario, IReadOnlyList<Captured> calls)
    {
        var exchanges = new JsonArray();
        foreach (var call in calls)
        {
            var request = new JsonObject { ["method"] = call.Method, ["path"] = call.Path };
            if (call.RequestHeaders.Count > 0)
            {
                request["headers"] = new JsonObject(call.RequestHeaders.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value))));
            }

            var reply = new JsonObject
            {
                ["status"] = call.Status,
                ["headers"] = new JsonObject(call.ReplyHeaders.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value))))
            };
            if (call.Body is not null)
            {
                reply["body"] = call.Body.DeepClone();
            }
            else if (call.BodyText is not null)
            {
                reply["bodyText"] = call.BodyText;
            }

            exchanges.Add(new JsonObject { ["request"] = request, ["reply"] = reply });
        }

        var recording = new JsonObject
        {
            ["app"] = "deluno",
            ["version"] = RecordedVersion,
            ["state"] = scenario.State,
            ["recordedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["note"] = scenario.Note + " Recorded from the real server code through the in-process test host (ApplicationTestHost), by DelunoRepliesToWeirTests; generated ids, times and the data folder are replaced by <id>, <utc> and <data-root>.",
            ["exchanges"] = exchanges
        };
        var folder = Path.Combine(RecordedReplies.Root, "deluno", RecordedVersion);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, scenario.State + ".json"),
            recording.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n",
            new UTF8Encoding(false));
    }
}
