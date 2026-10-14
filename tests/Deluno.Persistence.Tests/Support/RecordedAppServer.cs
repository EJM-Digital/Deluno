using System.Net;
using System.Text;
using Deluno.Tests.Recorded;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// Another app, answering over a real loopback port with the replies that app really gave (tests/fixtures/recorded).
/// The application under test is told to find the app at <see cref="BaseUrl"/> and talks to it exactly as it would to
/// the real one: its real HTTP client, its real adapter. Each call is answered by the recorded reply to the same
/// operation (<see cref="RecordedReplies.Handler"/>); a call no recording answers is answered 599 and written to
/// <see cref="Unanswered"/>, so a test that sends something nobody ever recorded fails and says what to record.
/// </summary>
internal sealed class RecordedAppServer : IAsyncDisposable
{
    private readonly WebApplication _server;
    private readonly HttpMessageHandler _handler;

    private RecordedAppServer(WebApplication server, HttpMessageHandler handler, Uri baseUri, List<Seen> requests, List<string> unanswered)
    {
        _server = server;
        _handler = handler;
        BaseUri = baseUri;
        Requests = requests;
        Unanswered = unanswered;
    }

    /// <summary>One request the app received.</summary>
    public sealed record Seen(string Method, string PathAndQuery, string Body);

    public Uri BaseUri { get; }

    public string BaseUrl => BaseUri.ToString().TrimEnd('/');

    public string Host => BaseUri.Host;

    public int Port => BaseUri.Port;

    /// <summary>Every request in the order it came, for a test to ask what Deluno really sent.</summary>
    public List<Seen> Requests { get; }

    /// <summary>Requests no recording answers.</summary>
    public List<string> Unanswered { get; }

    /// <summary>
    /// For an app whose answer to the same question changes as Deluno acts (a torrent list before and after a removal): each call
    /// whose address contains <paramref name="pathContains"/> is answered with the next recorded exchange's reply, the last one
    /// again once they run out. Pass the exchanges of the recordings that hold the before and after replies.
    /// </summary>
    public static Func<HttpRequestMessage, string, HttpResponseMessage?> InOrder(string pathContains, params Exchange[] replies)
    {
        var next = 0;
        return (request, _) =>
        {
            if (request.RequestUri is null || !request.RequestUri.PathAndQuery.Contains(pathContains, StringComparison.Ordinal))
            {
                return null;
            }

            var reply = replies[Math.Min(Interlocked.Increment(ref next) - 1, replies.Length - 1)];
            return reply.Reply.ToResponse();
        };
    }

    /// <summary>
    /// For an app that answers every kind of question at one address and tells them apart by a query value (Torznab's <c>t=</c>):
    /// a call whose <paramref name="parameter"/> has a listed value is answered with that recorded exchange's reply.
    /// </summary>
    public static Func<HttpRequestMessage, string, HttpResponseMessage?> ByQuery(string parameter, params (string Value, Exchange Reply)[] answers)
        => (request, _) =>
        {
            var value = System.Web.HttpUtility.ParseQueryString(request.RequestUri?.Query ?? string.Empty)[parameter];
            return answers.FirstOrDefault(answer => string.Equals(answer.Value, value, StringComparison.Ordinal)).Reply?.Reply.ToResponse();
        };

    public static async Task<RecordedAppServer> StartAsync(
        string app,
        IEnumerable<Recording> recordings,
        Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept = null,
        string? addressInRecordings = null)
    {
        RecordedAppServer? self = null;
        var requests = new List<Seen>();
        var unanswered = new List<string>();
        var handler = RecordedReplies.Handler(app, recordings, intercept);
        var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var server = builder.Build();
        server.Run(async context =>
        {
            context.Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
            }

            lock (requests)
            {
                requests.Add(new Seen(context.Request.Method, context.Request.Path + context.Request.QueryString, body));
            }

            var message = new HttpRequestMessage(new HttpMethod(context.Request.Method), $"http://127.0.0.1{context.Request.Path}{context.Request.QueryString}")
            {
                Content = new StringContent(body, Encoding.UTF8, context.Request.ContentType?.Split(';')[0] ?? "text/plain")
            };
            HttpResponseMessage reply;
            try
            {
                reply = await invoker.SendAsync(message, context.RequestAborted);
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
            {
                lock (unanswered)
                {
                    unanswered.Add($"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}: {exception.Message}");
                }

                context.Response.StatusCode = 599;
                await context.Response.WriteAsync(exception.Message);
                return;
            }

            context.Response.StatusCode = (int)reply.StatusCode;
            foreach (var header in reply.Headers)
            {
                if (!string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            if (reply.Content is not null)
            {
                foreach (var header in reply.Content.Headers)
                {
                    if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }

                var bytes = await reply.Content.ReadAsByteArrayAsync();
                var type = reply.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (addressInRecordings is not null && self is not null && (type.Contains("xml", StringComparison.Ordinal) || type.Contains("json", StringComparison.Ordinal) || type.StartsWith("text/", StringComparison.Ordinal)))
                {
                    // A reply that names the app's own address (Jackett's download links) is made to name this server instead.
                    bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(addressInRecordings, self.BaseUrl, StringComparison.Ordinal));
                }

                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes);
            }
        });
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new RecordedAppServer(server, handler, new Uri(address), requests, unanswered);
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _handler.Dispose();
    }
}
