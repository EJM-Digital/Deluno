using System.Text;
using Deluno.Tests.Recorded;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Support;

/// <summary>What the recorded Jellyfin says its sessions are doing.</summary>
internal enum JellyfinSessions { Playing, Idle, Refused }

/// <summary>
/// A local server on a loopback port that answers as Jellyfin 12.2.0 recorded (tests/fixtures/recorded/jellyfin/12.2.0): its identity,
/// libraries and users, its film catalogue (Sintel), what nobody has watched, its session list while Sintel plays, while nothing plays
/// and while it refuses the sign-in, and the calls that make, list and delete a collection. The application under test talks to it
/// over HTTP; nothing in between is replaced. A test can hold the catalogue answer back to stand for a server that is slow to answer.
/// </summary>
internal sealed class RecordedJellyfinServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly List<string> requests = [];
    private readonly List<string> updateBodies = [];
    private readonly object gate = new();

    private RecordedJellyfinServer(WebApplication app, string baseUrl)
    {
        this.app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    /// <summary>Which recorded session list the server answers with.</summary>
    public JellyfinSessions Sessions { get; set; } = JellyfinSessions.Idle;

    /// <summary>While set, the film catalogue is not answered until it is released: a server still reading its library.</summary>
    public TaskCompletionSource? CatalogueHeldBack { get; set; }

    /// <summary>The body of every call to the update list (Library/Media/Updated), as the application sent it.</summary>
    public IReadOnlyList<string> UpdateBodies { get { lock (gate) return updateBodies.ToArray(); } }

    /// <summary>Every call the application made, as "METHOD /path?query".</summary>
    public IReadOnlyList<string> Requests { get { lock (gate) return requests.ToArray(); } }

    public int Count(string fragment) => Requests.Count(request => request.Contains(fragment, StringComparison.Ordinal));

    public static async Task<RecordedJellyfinServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        RecordedJellyfinServer? server = null;
        app.Run(async context =>
        {
            lock (server!.gate) server.requests.Add(context.Request.Method + " " + context.Request.Path + context.Request.QueryString);
            if (context.Request.Method == "POST" && context.Request.Path == "/Library/Media/Updated")
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var posted = await reader.ReadToEndAsync();
                lock (server.gate) server.updateBodies.Add(posted);
            }

            if (context.Request.Method == "GET" && context.Request.Path == "/Items" && server.CatalogueHeldBack is { } held)
            {
                await held.Task;
            }

            var reply = server.ReplyFor(context.Request.Method, context.Request.Path, context.Request.QueryString.Value ?? string.Empty);
            if (reply is null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = reply.Headers.TryGetValue("content-type", out var type) ? type : "application/json";
            await context.Response.WriteAsync(reply.Body ?? string.Empty, Encoding.UTF8);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new RecordedJellyfinServer(app, address.TrimEnd('/'));
        return server;
    }

    private RecordedReply? ReplyFor(string method, string path, string query)
    {
        if (method == "GET" && path == "/System/Info") return RecordedReplies.Jellyfin("identity").Exchanges[1].Reply;
        if (method == "GET" && path == "/Library/VirtualFolders") return RecordedReplies.Jellyfin("libraries").Exchanges[0].Reply;
        if (method == "GET" && path == "/Users") return RecordedReplies.Jellyfin("libraries").Exchanges[1].Reply;
        if (method == "GET" && path == "/Items" && query.Contains("Ids=", StringComparison.Ordinal)) return RecordedReplies.Jellyfin("collection-sync").Exchanges[1].Reply;
        if (method == "GET" && path == "/Items") return RecordedReplies.Jellyfin("catalogue-movies").Exchanges.First(e => e.Request.Path.StartsWith("/Items", StringComparison.Ordinal)).Reply;
        if (method == "GET" && path.StartsWith("/Users/", StringComparison.Ordinal) && path.EndsWith("/Items", StringComparison.Ordinal))
        {
            if (query.Contains("BoxSet", StringComparison.Ordinal)) return RecordedReplies.Jellyfin("collection-sync").Exchanges[6].Reply;
            if (query.Contains("Playlist", StringComparison.Ordinal)) return RecordedReplies.Jellyfin("collection-sync").Exchanges[8].Reply;
            if (query.Contains("ParentId=", StringComparison.Ordinal)) return RecordedReplies.Jellyfin("collection-sync").Exchanges[7].Reply;
            // What the one person has and has not watched (the recording of both lists: nothing in this library is watched).
            return RecordedReplies.Jellyfin("watched").Exchanges[query.Contains("IsPlayed=true", StringComparison.Ordinal) ? 1 : 2].Reply;
        }

        if (method == "POST" && path == "/Library/Media/Updated") return RecordedReplies.Jellyfin("refresh").Exchanges[1].Reply;
        if (method == "POST" && path == "/Collections") return RecordedReplies.Jellyfin("collection-sync").Exchanges[0].Reply;
        if (method == "POST" && path.StartsWith("/Collections/", StringComparison.Ordinal)) return RecordedReplies.Jellyfin("collection-sync").Exchanges[4].Reply;
        if (method == "POST" && path.StartsWith("/Items/", StringComparison.Ordinal)) return RecordedReplies.Jellyfin("collection-sync").Exchanges[2].Reply;
        if (method == "DELETE" && path.StartsWith("/Items/", StringComparison.Ordinal)) return RecordedReplies.Jellyfin("collection-deleted").Exchanges[0].Reply;
        if (method == "GET" && path == "/Sessions")
        {
            return Sessions switch
            {
                JellyfinSessions.Playing => RecordedReplies.Jellyfin("sessions-playing").Exchanges[2].Reply,
                JellyfinSessions.Idle => RecordedReplies.Jellyfin("sessions-idle").Exchanges[0].Reply,
                _ => RecordedReplies.Jellyfin("wrong-token").Exchanges[0].Reply
            };
        }

        return null;
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}
