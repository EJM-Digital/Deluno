using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Processors.WeirContract;

/// <summary>
/// The shared parts of the Deluno-Weir contract tests (#1239): Weir's recorded replies (tests/fixtures/recorded/weir)
/// replayed through the real <see cref="ProcessorConnectionService"/>, and what Deluno sent kept so it can be held
/// against the recorded request. Nothing here writes a Weir reply; every reply a test uses is a recording.
/// </summary>
internal static class WeirContract
{
    public const string Secret = "shared-secret";

    public static readonly ProcessorConnectionItem Connection = new(
        "connection-1", "Weir", "generic-webhook", "http://weir:8788/api/v1/intake/webhook/deluno",
        "X-Webhook-Secret", Secret, true, "healthy", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    /// <summary>One recorded state, from the newest version recorded.</summary>
    public static Recording Rec(string state) => RecordedReplies.Weir(state);

    /// <summary>The hand-off id a recording is about: the one in the address of its first call about a hand-off, or in its hand-off body.</summary>
    public static string HandoffIdOf(Recording recording, int exchange = 0)
    {
        var request = recording.Exchanges[exchange].Request;
        var fromPath = Regex.Match(request.Path, @"/handoffs/deluno/([0-9a-fA-F-]{32,36})");
        if (fromPath.Success)
        {
            return fromPath.Groups[1].Value;
        }

        return JsonNode.Parse(request.Body!)!["handoffId"]!.GetValue<string>();
    }

    /// <summary>The recorded exchange for a kind of call: its method, and a path ending in the text (an id is part of the path, so the end is the kind).</summary>
    public static Exchange ExchangeOf(Recording recording, string method, string pathEndsWith, int skip = 0)
        => recording.Exchanges
            .Where(e => e.Request.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && e.Request.Path.EndsWith(pathEndsWith, StringComparison.Ordinal))
            .Skip(skip)
            .FirstOrDefault()
            ?? throw new KeyNotFoundException($"{recording.State} has no recorded {method} ending {pathEndsWith}.");

    public static ProcessorConnectionService Service(WeirReplay replay, IProcessorHandoffTitleResolver? resolver = null)
        => new(new Factory(new HttpClient(replay)), resolver);

    /// <summary>Weir's capabilities, which Deluno asks before it tells Weir an outcome, plus the recordings of the test.</summary>
    public static WeirReplay ReplayOf(params string[] states)
        => new(states.Select(Rec).ToArray());

    /// <summary>A body as the JSON a processor would read it: object members in order, the instant as an instant.</summary>
    public static JsonNode Body(string? text) => JsonNode.Parse(text ?? throw new InvalidOperationException("No body."))!;

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

/// <summary>Answers each call with the recorded reply to the same method and address, keeps what was sent, and fails on a call that was never recorded.</summary>
internal sealed class WeirReplay(params Recording[] recordings) : HttpMessageHandler
{
    private readonly List<SentRequest> _sent = [];

    /// <summary>When set, no call is answered: the connection fails as if Weir were switched off.</summary>
    public bool Down { get; set; }

    /// <summary>
    /// Match a call to a recorded one by its kind only, whatever hand-off id it names. For a test that carries one hand-off
    /// through several recorded situations (queued, then working, then finished), each recorded with its own id.
    /// </summary>
    public bool IgnoreIds { get; set; }

    public IReadOnlyList<SentRequest> Sent => _sent;

    private static string Kind(string path) => System.Text.RegularExpressions.Regex.Replace(path, "[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}", "{id}");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        _sent.Add(new SentRequest(request.Method.Method, request.RequestUri!.PathAndQuery, body, headers));
        if (Down)
        {
            throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
        }

        foreach (var recording in recordings)
        {
            foreach (var exchange in recording.Exchanges)
            {
                if (exchange.Request.Method.Equals(request.Method.Method, StringComparison.OrdinalIgnoreCase)
                    && (IgnoreIds
                        ? Kind(exchange.Request.Path) == Kind(request.RequestUri.PathAndQuery)
                        : exchange.Request.Path == request.RequestUri.PathAndQuery))
                {
                    return exchange.Reply.ToResponse();
                }
            }
        }

        throw new InvalidOperationException($"{request.Method} {request.RequestUri.PathAndQuery} was never recorded for ({string.Join(", ", recordings.Select(r => r.State))}).");
    }
}

internal sealed record SentRequest(string Method, string Path, string? Body, IReadOnlyDictionary<string, string> Headers);
