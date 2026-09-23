using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #606: searches every public tracker Deluno ships, live. Off unless
/// DELUNO_LIVE_TRACKERS is set ("1" for all, or a comma list of ids), because
/// it depends on the internet and on sites that come and go; it is the proof
/// the engine reads real pages, and the list of which trackers answer from
/// where it runs. DELUNO_LIVE_BODY adds the start of each page to the trace.
/// </summary>
public sealed class TrackerLiveProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task Every_public_tracker_answers_a_real_search()
    {
        if (Environment.GetEnvironmentVariable("DELUNO_LIVE_TRACKERS") is not { Length: > 0 } filter)
        {
            return;
        }

        var catalog = new TrackerDefinitionCatalog(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);
        var definitions = catalog.List()
            .Where(summary => summary.Unsupported is null && (filter != "1" || summary.Privacy == "public" && (summary.Movies || summary.Tv)))
            .Where(summary => filter == "1" || filter.Split(',').Contains(summary.Id))
            .Select(summary => catalog.Find(summary.Id)!)
            .ToArray();

        var results = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(definitions, new ParallelOptions { MaxDegreeOfParallelism = 12 }, async (definition, token) =>
        {
            // One engine per tracker, so each trace holds only its own requests.
            var trace = new ConcurrentQueue<string>();
            var engine = new TrackerEngine(
                cookies => new TracingHandler(new SocketsHttpHandler { CookieContainer = cookies, AutomaticDecompression = System.Net.DecompressionMethods.All }, trace),
                TimeProvider.System,
                NullLogger<TrackerEngine>.Instance);
            var connection = new TrackerConnection("probe-" + definition.Id, definition, definition.Links.FirstOrDefault() ?? string.Empty, new Dictionary<string, string?>());
            var movies = TrackerCategories.SiteCategoriesFor(definition, [2000]).Count > 0;
            var query = Environment.GetEnvironmentVariable("DELUNO_LIVE_QUERY") is { Length: > 0 } custom
                ? new TrackerQuery(custom, "search")
                : movies
                    ? new TrackerQuery("Night of the Living Dead 1968", "movie", Year: 1968, ImdbId: "tt0063350", Categories: [2000])
                    : new TrackerQuery("The Simpsons S01E01", "tvsearch", Season: 1, Episode: 1, Categories: [5000]);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                var result = await engine.SearchAsync(connection, query, timeout.Token);
                var first = result.Releases.FirstOrDefault();
                results.Add($"{(result.Releases.Count > 0 ? "OK  " : "NONE")} {definition.Id}: {result.Releases.Count} releases, {result.RowsSkipped} rows skipped"
                            + (first is null ? string.Empty : $" | {first.Title} | {first.Size} | S{first.Seeders} | {first.PublishDate:yyyy-MM-dd} | {Shorten(first.Link)}")
                            + (result.Problems.Count > 0 ? $" | problem: {result.Problems[0]}" : string.Empty)
                            + (first is null || Environment.GetEnvironmentVariable("DELUNO_LIVE_TRACE") is not null ? " | " + string.Join(" ; ", trace) : string.Empty));
            }
            catch (Exception exception)
            {
                results.Add($"FAIL {definition.Id}: {exception.GetType().Name}: {exception.Message} | {string.Join(" ; ", trace)}");
            }
        });

        foreach (var line in results.OrderBy(line => line, StringComparer.Ordinal))
        {
            output.WriteLine(line);
        }
    }

    private static string? Shorten(string? link) => link is { Length: > 90 } ? link[..90] + "…" : link;

    private sealed class TracingHandler(HttpMessageHandler inner, ConcurrentQueue<string> trace) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            var body = System.Text.Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            var sample = Environment.GetEnvironmentVariable("DELUNO_LIVE_BODY") is not null
                ? " " + Regex.Replace(body, @"\s+", " ")[..Math.Min(body.Length, 1500)]
                : string.Empty;
            trace.Enqueue($"{request.Method} {request.RequestUri} -> {(int)response.StatusCode} {body.Length}b{sample}");
            return response;
        }
    }
}
