using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// #521: the Leaving soon collection on each kind of server holds exactly what
/// Deluno asks for - created when needed, put back after a hand edit, deleted
/// when empty - and nothing but a collection is ever deleted.
/// </summary>
public sealed class LeavingSoonCollectionClientTests
{
    private const string Name = "Leaving soon";

    private static MediaServerConnection Server(string kind)
        => new("server-1", kind, "Rig " + kind, "http://192.168.1.51:8096", "token-123", true, "healthy", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static MediaServerCollectionRequest Request(string mediaType, bool home, params MediaServerCollectionMember[] members)
        => new(mediaType, Name, "Films Deluno will remove soon.", members, home);

    // ------------------------------------------------------------ Jellyfin / Emby

    [Fact]
    public async Task Jellyfin_creates_the_collection_with_its_titles_and_says_what_it_is()
    {
        var jellyfin = new FakeJellyfin();
        var client = new JellyfinMediaServerClient(Factory(jellyfin.Handle));

        var answer = await client.SyncCollectionAsync(Server(MediaServerKinds.Jellyfin), Request("movies", false, new MediaServerCollectionMember("m1", "movie"), new MediaServerCollectionMember("m2", "movie")), CancellationToken.None);

        var sync = Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(answer).Value;
        Assert.Equal((2, 0, 2, false), (sync.Added, sync.Removed, sync.Members, sync.Deleted));
        var collection = Assert.Single(jellyfin.Collections);
        Assert.Equal(["m1", "m2"], collection.Members.Order());
        Assert.Equal("Films Deluno will remove soon.", collection.Overview);
    }

    [Fact]
    public async Task Jellyfin_puts_back_a_collection_edited_by_hand()
    {
        var jellyfin = new FakeJellyfin();
        jellyfin.Collections.Add(new FakeCollection("bs1", Name) { Members = { "m1", "m9" } });
        var client = new JellyfinMediaServerClient(Factory(jellyfin.Handle));

        var answer = await client.SyncCollectionAsync(Server(MediaServerKinds.Jellyfin), Request("movies", false, new MediaServerCollectionMember("m1", "movie"), new MediaServerCollectionMember("m2", "movie")), CancellationToken.None);

        var sync = Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(answer).Value;
        Assert.Equal((1, 1), (sync.Added, sync.Removed));
        Assert.Equal(["m1", "m2"], Assert.Single(jellyfin.Collections).Members.Order());
        // Its description was missing, so it is set now.
        Assert.Equal("Films Deluno will remove soon.", jellyfin.Collections[0].Overview);
    }

    [Fact]
    public async Task Jellyfin_favourites_the_collection_onto_home_when_asked_and_unfavourites_it_when_not()
    {
        var jellyfin = new FakeJellyfin();
        var client = new JellyfinMediaServerClient(Factory(jellyfin.Handle));

        var created = await client.SyncCollectionAsync(Server(MediaServerKinds.Jellyfin), Request("movies", true, new MediaServerCollectionMember("m1", "movie")), CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(created);
        var collection = Assert.Single(jellyfin.Collections);
        Assert.Contains(collection.Id, jellyfin.Favourites);

        var updated = await client.SyncCollectionAsync(Server(MediaServerKinds.Jellyfin), Request("movies", false, new MediaServerCollectionMember("m1", "movie")), CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(updated);
        Assert.DoesNotContain(collection.Id, jellyfin.Favourites);
    }

    [Fact]
    public async Task Emby_deletes_the_emptied_collection_and_never_anything_else_by_that_name()
    {
        var emby = new FakeJellyfin(prefix: "/emby");
        emby.Collections.Add(new FakeCollection("bs1", Name) { Members = { "m1" } });
        // A film that happens to share the name is not a collection.
        emby.Movies.Add(("m5", Name));
        var client = new EmbyMediaServerClient(Factory(emby.Handle));

        var answer = await client.SyncCollectionAsync(Server(MediaServerKinds.Emby), Request("movies", false), CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(answer).Value.Deleted);
        Assert.Empty(emby.Collections);
        Assert.Equal(["/emby/Items/bs1"], emby.Deletes);
    }

    [Fact]
    public async Task A_Jellyfin_that_cannot_be_reached_could_not_be_updated()
    {
        var client = new JellyfinMediaServerClient(Factory(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var answer = await client.SyncCollectionAsync(Server(MediaServerKinds.Jellyfin), Request("movies", false, new MediaServerCollectionMember("m1", "movie")), CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Unreachable>(answer);
    }

    // ------------------------------------------------------------------- Plex

    [Fact]
    public async Task Plex_keeps_one_collection_per_section_and_shows_it_on_home_when_asked()
    {
        var plex = new FakePlex();
        var client = new PlexMediaServerClient(Factory(plex.Handle));

        var answer = await client.SyncCollectionAsync(
            Server(MediaServerKinds.Plex),
            Request("tv", true, new MediaServerCollectionMember("s1", "show", "2"), new MediaServerCollectionMember("s2", "show", "3"), new MediaServerCollectionMember("season9", "season", "2")),
            CancellationToken.None);

        var sync = Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(answer).Value;
        // A season cannot sit in a show collection on Plex.
        Assert.Equal((2, 2, 1), (sync.Added, sync.Members, sync.Skipped));
        Assert.Equal(["2", "3"], plex.Collections.Values.Select(collection => collection.Section).Order());
        Assert.All(plex.Collections.Values, collection =>
        {
            Assert.Equal(2, collection.Type);
            Assert.True(collection.OnHome);
            Assert.Equal("Films Deluno will remove soon.", collection.Summary);
        });
        Assert.Contains("server://machine-1/com.plexapp.plugins.library/library/metadata/s1", plex.Uris);
    }

    [Fact]
    public async Task Plex_puts_back_a_hand_edit_takes_it_off_home_and_deletes_it_when_empty()
    {
        var plex = new FakePlex();
        plex.Collections["c1"] = new FakePlexCollection("1", 1, Name) { Members = { "m1", "m7" }, OnHome = true };
        var client = new PlexMediaServerClient(Factory(plex.Handle));

        var edited = await client.SyncCollectionAsync(Server(MediaServerKinds.Plex), Request("movies", false, new MediaServerCollectionMember("m1", "movie", "1"), new MediaServerCollectionMember("m2", "movie", "1")), CancellationToken.None);

        var sync = Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(edited).Value;
        Assert.Equal((1, 1), (sync.Added, sync.Removed));
        Assert.Equal(["m1", "m2"], plex.Collections["c1"].Members.Order());
        Assert.False(plex.Collections["c1"].OnHome);

        var emptied = await client.SyncCollectionAsync(Server(MediaServerKinds.Plex), Request("movies", false), CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(emptied).Value.Deleted);
        Assert.Empty(plex.Collections);
    }

    // ------------------------------------------------------------------ fakes

    private sealed class FakeCollection(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public HashSet<string> Members { get; } = [];
        public string? Overview { get; set; }
    }

    /// <summary>Jellyfin's collection API as Deluno uses it; Emby's is the same under <c>/emby</c>.</summary>
    private sealed class FakeJellyfin(string prefix = "")
    {
        public List<FakeCollection> Collections { get; } = [];
        public List<(string Id, string Name)> Movies { get; } = [];
        public List<string> Deletes { get; } = [];
        public HashSet<string> Favourites { get; } = [];
        private int _next = 1;

        public HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.StartsWith(prefix, path, StringComparison.Ordinal);
            path = path[prefix.Length..];
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);

            if (request.Method == HttpMethod.Get && path == "/Items" && query["IncludeItemTypes"] == "BoxSet")
            {
                // Search matches films too; the type is what tells them apart.
                var found = Collections.Select(c => new { Id = c.Id, Name = c.Name, Type = "BoxSet", Overview = c.Overview })
                    .Concat(Movies.Select(m => new { Id = m.Id, Name = m.Name, Type = "Movie", Overview = (string?)null }));
                return Page(found);
            }

            if (request.Method == HttpMethod.Get && path == "/Users")
            {
                return Json("""[{"Id":"u1","Name":"delunolab"}]""");
            }

            // As real Jellyfin does: a collection's members are only listed for a user.
            if (request.Method == HttpMethod.Get && path == "/Items" && query["ParentId"] is not null)
            {
                return Page(Array.Empty<object>());
            }

            if (request.Method == HttpMethod.Get && path == "/Users/u1/Items" && query["ParentId"] is { } parent)
            {
                return Page(Collections.Single(c => c.Id == parent).Members.Select(id => new { Id = id }));
            }

            if (request.Method == HttpMethod.Get && path == "/Items" && query["Ids"] is { } ids)
            {
                return Page(Collections.Where(c => c.Id == ids).Select(c => new { Id = c.Id, Name = c.Name, Overview = c.Overview }));
            }

            if (request.Method == HttpMethod.Post && path == "/Collections")
            {
                var collection = new FakeCollection($"bs{_next++}", query["Name"]!);
                foreach (var id in query["Ids"]!.Split(',')) collection.Members.Add(id);
                Collections.Add(collection);
                return Json($$"""{"Id":"{{collection.Id}}"}""");
            }

            if (path.StartsWith("/Collections/", StringComparison.Ordinal) && path.EndsWith("/Items", StringComparison.Ordinal))
            {
                var collection = Collections.Single(c => c.Id == path.Split('/')[2]);
                foreach (var id in query["Ids"]!.Split(','))
                {
                    if (request.Method == HttpMethod.Post) collection.Members.Add(id);
                    else collection.Members.Remove(id);
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Post && path.StartsWith("/Items/", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
                Collections.Single(c => c.Id == path.Split('/')[2]).Overview = body.RootElement.GetProperty("Overview").GetString();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith("/Items/", StringComparison.Ordinal))
            {
                Deletes.Add(request.RequestUri.AbsolutePath);
                var id = path.Split('/')[2];
                Assert.Contains(Collections, c => c.Id == id);
                Collections.RemoveAll(c => c.Id == id);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.StartsWith("/Users/", StringComparison.Ordinal) && path.Contains("/FavoriteItems/", StringComparison.Ordinal))
            {
                var itemId = path.Split('/')[4];
                if (request.Method == HttpMethod.Post) Favourites.Add(itemId);
                else if (request.Method == HttpMethod.Delete) Favourites.Remove(itemId);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Page<T>(IEnumerable<T> items)
        {
            var list = items.ToArray();
            return Json(JsonSerializer.Serialize(new { Items = list, TotalRecordCount = list.Length }));
        }
    }

    private sealed class FakePlexCollection(string section, int type, string title)
    {
        public string Section { get; } = section;
        public int Type { get; } = type;
        public string Title { get; } = title;
        public HashSet<string> Members { get; } = [];
        public bool OnHome { get; set; }
        public string? Summary { get; set; }
    }

    private sealed class FakePlex
    {
        public Dictionary<string, FakePlexCollection> Collections { get; } = [];
        public List<string> Uris { get; } = [];
        private int _next = 1;

        public HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            string Keys(string uri)
            {
                Uris.Add(uri);
                return uri[(uri.LastIndexOf('/') + 1)..];
            }

            // Plex 1.43.4's own replies (tests/fixtures/recorded/plex), unclaimed, changed only in the value the test is about: its machine id,
            // a third library (a second TV library, copied from the recorded TV one), and the collections and children a test holds.
            // The home-screen promotion (/hubs/sections/{id}/manage) answers 404 on an unclaimed server and a claimed server's reply is not
            // recorded (MISSING.md, 'Plex, claimed'), so that one reply below is still written here.
            if (path == "/") return Json(RecordedReplies.Plex("root").Single.Reply.BodyWith(("MediaContainer.machineIdentifier", "machine-1")));
            if (path == "/library/sections")
                return Json(RecordedReplies.Plex("sections").Body("/library/sections",
                    ("+MediaContainer.Directory", 3), ("MediaContainer.Directory.2.key", "3"), ("MediaContainer.Directory.2.type", "show")));

            if (request.Method == HttpMethod.Get && path.StartsWith("/library/sections/", StringComparison.Ordinal) && path.EndsWith("/collections", StringComparison.Ordinal))
            {
                var section = path.Split('/')[3];
                return Listing(RecordedReplies.Plex("collection-sync").Find("collections?includeGuids").Reply,
                    Collections.Where(c => c.Value.Section == section).Select(c => (c.Key, (string?)c.Value.Title)));
            }

            if (request.Method == HttpMethod.Post && path == "/library/collections")
            {
                Assert.Equal("0", query["smart"]);
                var key = $"c{_next++}";
                var collection = new FakePlexCollection(query["sectionId"]!, int.Parse(query["type"]!), query["title"]!);
                foreach (var id in Keys(query["uri"]!).Split(',')) collection.Members.Add(id);
                Collections[key] = collection;
                return Listing(RecordedReplies.Plex("collection-sync").Find("library/collections?type", "POST").Reply, [(key, (string?)collection.Title)]);
            }

            if (request.Method == HttpMethod.Put && path.StartsWith("/library/sections/", StringComparison.Ordinal) && path.EndsWith("/all", StringComparison.Ordinal))
            {
                Assert.Equal("18", query["type"]);
                Collections[query["id"]!].Summary = query["summary.value"];
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path.StartsWith("/library/collections/", StringComparison.Ordinal))
            {
                var parts = path.Split('/');
                var collection = Collections[parts[3]];
                if (request.Method == HttpMethod.Get && parts.Length == 5 && parts[4] == "children")
                    return Listing(RecordedReplies.Plex("collection-sync").Find("/children").Reply, collection.Members.Select(id => (id, (string?)null)));
                if (request.Method == HttpMethod.Put && parts.Length == 5 && parts[4] == "items")
                {
                    foreach (var id in Keys(query["uri"]!).Split(',')) collection.Members.Add(id);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                if (request.Method == HttpMethod.Delete && parts.Length == 6 && parts[4] == "items")
                {
                    collection.Members.Remove(parts[5]);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                if (request.Method == HttpMethod.Delete && parts.Length == 4)
                {
                    Collections.Remove(parts[3]);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
            }

            if (path.StartsWith("/hubs/sections/", StringComparison.Ordinal) && path.Contains("/manage", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get)
                {
                    var collection = Collections[query["metadataItemId"]!];
                    return Json(JsonSerializer.Serialize(new { MediaContainer = new { Hub = collection.OnHome ? new[] { new { identifier = $"custom.collection.{query["metadataItemId"]}", promotedToOwnHome = true } } : [] } }));
                }

                if (request.Method == HttpMethod.Post)
                {
                    Collections[query["metadataItemId"]!].OnHome = true;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                if (request.Method == HttpMethod.Delete)
                {
                    Collections[path[(path.LastIndexOf('.') + 1)..]].OnHome = false;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        /// <summary>The recorded reply with as many items as the test holds: the recorded first item copied, then only its key (and title) set.</summary>
        private static HttpResponseMessage Listing(RecordedReply recorded, IEnumerable<(string Key, string? Title)> items)
        {
            var list = items.ToArray();
            var edits = new List<(string, object?)> { ("MediaContainer.size", list.Length) };
            if (list.Length == 0)
            {
                edits.Add(("^MediaContainer.Metadata", 0));
            }
            else
            {
                edits.Add(("+MediaContainer.Metadata", list.Length));
                for (var i = 0; i < list.Length; i++)
                {
                    edits.Add(($"MediaContainer.Metadata.{i}.ratingKey", list[i].Key));
                    if (list[i].Title is not null) edits.Add(($"MediaContainer.Metadata.{i}.title", list[i].Title));
                }
            }

            return Json(recorded.BodyWith(edits.ToArray()));
        }
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static IHttpClientFactory Factory(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new StubFactory(new HttpClient(new StubHandler(send)));

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
