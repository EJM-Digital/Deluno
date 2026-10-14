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
/// A real local HTTP server that answers like a recorded Torznab/Newznab indexer (Jackett 0.24.2850 or NZBHydra2 9.1.1):
/// each call gets the recorded reply to the call with the same function (<c>t=</c>), exactly as the real app sent it, so
/// Deluno's real clients, resilience and recorders run against what the app really says. Nothing is written by hand.
/// <see cref="AnswerEverythingWith"/> makes it answer every call alike, as an indexer does for a wrong key.
/// </summary>
internal sealed class RecordedIndexerServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<string> _requests = [];

    private RecordedIndexerServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    /// <summary>The server's address (scheme, host and port).</summary>
    public string BaseUrl { get; }

    /// <summary>Every call as the server saw it: address and query, in order.</summary>
    public IReadOnlyList<string> Requests { get { lock (_requests) return _requests.ToArray(); } }

    /// <summary>The user agent of the last call.</summary>
    public string? LastUserAgent { get; private set; }

    /// <summary>When set, every call is answered with this one recorded reply.</summary>
    public RecordedReply? AnswerEverythingWith { get; set; }

    public static async Task<RecordedIndexerServer> StartAsync(params Recording[] recordings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        RecordedIndexerServer? server = null;
        app.Run(async context =>
        {
            lock (server!._requests) server._requests.Add(context.Request.Path + context.Request.QueryString);
            server.LastUserAgent = context.Request.Headers.UserAgent.ToString();
            var function = context.Request.Query["t"].ToString();
            var reply = server.AnswerEverythingWith
                ?? recordings.SelectMany(r => r.Exchanges)
                    .FirstOrDefault(e => FunctionOf(e.Request.Path) == function)?.Reply
                ?? throw new InvalidOperationException($"No recording ({string.Join(", ", recordings.Select(r => $"{r.App} {r.State}"))}) answers t={function}.");
            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = reply.Headers.TryGetValue("content-type", out var type) ? type : "application/xml";
            await context.Response.WriteAsync(reply.Body ?? string.Empty, Encoding.UTF8);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new RecordedIndexerServer(app, address.TrimEnd('/'));
        return server;
    }

    private static string FunctionOf(string path)
    {
        var query = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : string.Empty;
        return System.Web.HttpUtility.ParseQueryString(query)["t"] ?? string.Empty;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
