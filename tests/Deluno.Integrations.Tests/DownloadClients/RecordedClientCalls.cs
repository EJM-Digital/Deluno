using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Shared by the tests that replay a download client's recorded real replies (#1237, #1301): a client item, an HTTP client
/// factory over a recorded handler, and a list of every call the client made, so a test can say what was sent as well as
/// what came back.
/// </summary>
internal static class RecordedClientCalls
{
    public static DownloadClientItem Client(string protocol, string? username = null, string? secret = "secret")
        => new($"{protocol}-1", protocol, protocol, "localhost", 8080, username, secret, "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    public sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// A recorded client that notes every call (method, address and query, body). <paramref name="answer"/> sees each call first
    /// and may answer it from a recording changed in the one value the test is about; null leaves it to the recordings.
    /// </summary>
    public sealed class Calls(string app, Recording[] recordings, Func<HttpRequestMessage, string, HttpResponseMessage?>? answer = null)
    {
        public List<string> Log { get; } = [];

        public HttpMessageHandler Build() => RecordedReplies.Handler(app, recordings, (request, body) =>
        {
            Log.Add($"{request.Method} {request.RequestUri!.PathAndQuery} {body}".TrimEnd());
            return answer?.Invoke(request, body);
        });

        public bool Sent(string fragment) => Log.Any(line => line.Contains(fragment, StringComparison.Ordinal));

        public int Count(string fragment) => Log.Count(line => line.Contains(fragment, StringComparison.Ordinal));
    }
}
