using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// #493: Plex, Jellyfin and Emby, read the way each one actually answers - and
/// "could not ask" never mistaken for "nothing there".
/// </summary>
public sealed class MediaServerClientTests
{
    private static MediaServerConnection Server(string kind, string baseUrl = "http://192.168.1.51:8096")
        => new("server-1", kind, "Rig " + kind, baseUrl, "token-123", true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Jellyfin_combines_what_every_user_has_watched()
    {
        var requests = new List<string>();
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            // Jellyfin 12 refuses the legacy X-Emby-Token header (#493, found on the rig).
            Assert.Equal("MediaBrowser Token=\"token-123\"", Assert.Single(request.Headers.GetValues("Authorization")));
            Assert.False(request.Headers.Contains("X-Emby-Token"));
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path == "/Users") return Json("""[{"Id":"u1","Name":"James"},{"Id":"u2","Name":"Ben"},{"Id":"u3","Name":"Old","Policy":{"IsDisabled":true}}]""");
            if (path == "/Items") return Json(
                """
                {"Items":[
                {"Id":"m1","Type":"Movie","Name":"Sintel","ProductionYear":2010,"ProviderIds":{"Tmdb":"45745","Imdb":"tt1727587"},"Path":"\\\\nas\\media\\Deluno-E2E\\Movies\\Sintel (2010)\\Sintel.mkv","DateCreated":"2026-09-01T00:00:00Z",
                 "PremiereDate":"2010-09-27T00:00:00Z","Genres":["Animation","Fantasy"],"People":[{"Name":"Halina Reijn"}],"Tags":["open movie"],"CommunityRating":7.1,"CriticRating":88,
                 "MediaSources":[{"Bitrate":4200000,"MediaStreams":[{"Type":"Video","Width":1920,"Height":818,"Codec":"h264"},{"Type":"Audio"}]}]},
                {"Id":"m2","Type":"Movie","Name":"Big Buck Bunny","ProductionYear":2008,"ProviderIds":{"tmdb":"10378"},"Path":"/media/bbb.mkv"}],"TotalRecordCount":2}
                """);
            if (query.Contains("IncludeItemTypes=BoxSet", StringComparison.Ordinal)) return Json("""{"Items":[{"Id":"bs1","Name":"Blender films"}],"TotalRecordCount":1}""");
            if (query.Contains("ParentId=bs1", StringComparison.Ordinal)) return Json("""{"Items":[{"Id":"m1"}],"TotalRecordCount":1}""");
            if (query.Contains("IncludeItemTypes=Playlist", StringComparison.Ordinal)) return Json("""{"Items":[],"TotalRecordCount":0}""");
            return (path, query) switch
            {
                ("/Users/u1/Items", var q) when q.Contains("IsPlayed=true") => Json("""{"Items":[{"Id":"m1","UserData":{"Played":true,"PlayCount":2,"LastPlayedDate":"2026-09-10T20:00:00Z"}}],"TotalRecordCount":1}"""),
                ("/Users/u2/Items", var q) when q.Contains("IsPlayed=true") => Json("""{"Items":[{"Id":"m1","UserData":{"Played":true,"PlayCount":1,"LastPlayedDate":"2026-09-12T21:00:00Z"}}],"TotalRecordCount":1}"""),
                ("/Users/u2/Items", var q) when q.Contains("IsFavorite=true") => Json("""{"Items":[{"Id":"m2","UserData":{"Played":false,"IsFavorite":true}}],"TotalRecordCount":1}"""),
                ("/Users/u1/Items", var q) when q.Contains("IsResumable") => Json("""{"Items":[{"Id":"m2","UserData":{"Played":false,"PlaybackPositionTicks":1200000000}}],"TotalRecordCount":1}"""),
                (_, _) when path.StartsWith("/Users/", StringComparison.Ordinal) => Json("""{"Items":[],"TotalRecordCount":0}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "movies", CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value;
        Assert.Equal(["James", "Ben"], library.Users);
        var items = library.Items;
        var sintel = Assert.Single(items, item => item.ServerItemId == "m1");
        Assert.Equal(MediaServerItemLevels.Movie, sintel.Level);
        Assert.Equal("45745", sintel.TmdbId);
        Assert.Equal("tt1727587", sintel.ImdbId);
        Assert.True(sintel.Watch.PlayedByAnyone);
        Assert.True(sintel.Watch.PerUserKnown);
        Assert.Equal(["James", "Ben"], sintel.Watch.PlayedBy);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T21:00:00Z"), sintel.Watch.LastPlayedUtc);
        Assert.Equal(3, sintel.Viewers.Sum(viewer => viewer.PlayCount));
        Assert.Equal(["Animation", "Fantasy"], sintel.Genres);
        Assert.Equal(["Halina Reijn"], sintel.People);
        Assert.Equal(["open movie"], sintel.Labels);
        Assert.Equal(["Blender films"], sintel.Collections);
        Assert.Equal(8.8, sintel.Ratings.Critic);
        Assert.Equal(7.1, sintel.Ratings.Audience);
        Assert.Null(sintel.Ratings.Imdb);
        Assert.Equal(new MediaServerVideo("1080p", 4200, "h264"), sintel.Video);
        Assert.Equal(DateTimeOffset.Parse("2010-09-27T00:00:00Z"), sintel.ReleaseDate);

        var bunny = Assert.Single(items, item => item.ServerItemId == "m2");
        Assert.Equal("10378", bunny.TmdbId);
        Assert.False(bunny.Watch.PlayedByAnyone);
        Assert.Contains(bunny.Viewers, viewer => viewer is { User: "Ben", Favourite: true });
        Assert.Contains(bunny.Viewers, viewer => viewer is { User: "James", InProgress: true });
        Assert.DoesNotContain(requests, request => request.StartsWith("/Users/u3/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Jellyfin_TV_listing_holds_shows_seasons_and_episodes()
    {
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path == "/Users") return Json("""[{"Id":"u1","Name":"James"}]""");
            if (path == "/Items")
            {
                Assert.Contains("IncludeItemTypes=Series,Season,Episode", query, StringComparison.Ordinal);
                return Json(
                    """
                    {"Items":[
                    {"Id":"s1","Type":"Series","Name":"Breaking Bad","Path":"/tv/Breaking Bad"},
                    {"Id":"se1","Type":"Season","Name":"Season 1","SeriesId":"s1","IndexNumber":1},
                    {"Id":"e1","Type":"Episode","Name":"Pilot","SeriesId":"s1","SeasonId":"se1","ParentIndexNumber":1,"IndexNumber":1,"Path":"/tv/Breaking Bad/S01E01.mkv","PremiereDate":"2008-01-20T00:00:00Z"}],"TotalRecordCount":3}
                    """);
            }

            return Json("""{"Items":[],"TotalRecordCount":0}""");
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "tv", CancellationToken.None);

        var items = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items;
        Assert.Equal(MediaServerItemLevels.Show, Assert.Single(items, item => item.ServerItemId == "s1").Level);
        var season = Assert.Single(items, item => item.ServerItemId == "se1");
        Assert.Equal((MediaServerItemLevels.Season, "s1", 1), (season.Level, season.ShowServerItemId, season.SeasonNumber));
        var episode = Assert.Single(items, item => item.ServerItemId == "e1");
        Assert.Equal((MediaServerItemLevels.Episode, "s1", "se1", 1, 1), (episode.Level, episode.ShowServerItemId, episode.SeasonServerItemId, episode.SeasonNumber, episode.EpisodeNumber));
    }

    [Fact]
    public async Task An_empty_library_is_an_answer()
    {
        var client = new JellyfinMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users" => Json("""[{"Id":"u1","Name":"James"}]"""),
            _ => Json("""{"Items":[],"TotalRecordCount":0}""")
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "movies", CancellationToken.None);

        Assert.Empty(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_server_that_refuses_or_fails_could_not_be_asked(HttpStatusCode code)
    {
        var client = new EmbyMediaServerClient(Factory(_ => new HttpResponseMessage(code)));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Emby), "movies", CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(answer);
    }

    [Fact]
    public async Task A_server_that_is_down_could_not_be_asked()
    {
        var client = new PlexMediaServerClient(Factory(_ => throw new HttpRequestException("connection refused")));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var unreachable = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(answer);
        Assert.Contains("could not reach", unreachable.Reason, StringComparison.Ordinal);
    }

    /// <summary>A second page that fails must fail the whole answer, not hand back the first page as if it were everything.</summary>
    [Fact]
    public async Task A_listing_that_breaks_part_way_could_not_be_asked()
    {
        var calls = 0;
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users") return Json("""[{"Id":"u1","Name":"James"}]""");
            if (request.RequestUri.AbsolutePath != "/Items") return Json("""{"Items":[],"TotalRecordCount":0}""");
            if (calls++ > 0) return new HttpResponseMessage(HttpStatusCode.BadGateway);
            var page = string.Join(",", Enumerable.Range(0, 500).Select(i => $$"""{"Id":"m{{i}}","Name":"Film {{i}}"}"""));
            return Json($$"""{"Items":[{{page}}],"TotalRecordCount":900}""");
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "movies", CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(answer);
    }

    [Fact]
    public async Task Emby_is_asked_under_its_emby_path_and_says_who_it_is()
    {
        var paths = new List<string>();
        var client = new EmbyMediaServerClient(Factory(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/emby/System/Info" => Json("""{"ServerName":"RIG-EMBY","Id":"abc123","Version":"4.9.1.80"}"""),
                "/emby/Library/VirtualFolders" => Json("""[{"Name":"Movies"},{"Name":"TV"}]"""),
                "/emby/Users" => Json("""[{"Id":"u1","Name":"James"}]"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var result = await client.TestAsync(Server(MediaServerKinds.Emby, "http://192.168.1.51:8097"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("RIG-EMBY", result.ServerName);
        Assert.Equal("abc123", result.ServerId);
        Assert.Equal(2, result.LibraryCount);
        Assert.Contains("RIG-EMBY", result.Message, StringComparison.Ordinal);
        Assert.All(paths, path => Assert.StartsWith("/emby/", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_from_Jellyfin_reports_the_files_deleted_and_never_calls_its_delete()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            sent = request;
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var item = new MediaServerItem("m1", "movies", "Sintel", 2010, null, null, null, [@"\\nas\media\Deluno-E2E\Movies\Sintel (2010)\Sintel.mkv"], null, MediaServerWatchState.Unplayed);

        var answer = await client.RemoveAsync(Server(MediaServerKinds.Jellyfin), item, CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRemoval>.Reported>(answer);
        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal("/Library/Media/Updated", sent.RequestUri!.AbsolutePath);
        Assert.Contains("\"UpdateType\":\"Deleted\"", body, StringComparison.Ordinal);
        Assert.Contains("Sintel.mkv", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plex_reads_ids_files_scores_and_one_account_watch_state()
    {
        var client = new PlexMediaServerClient(Factory(request =>
        {
            Assert.Equal("token-123", Assert.Single(request.Headers.GetValues("X-Plex-Token")));
            if (request.RequestUri!.Host == "discover.provider.plex.tv")
            {
                return Json("""{"MediaContainer":{"totalSize":1,"Metadata":[{"guid":"plex://movie/5d776b"}]}}""");
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"},{"key":"2","type":"show"}]}}"""),
                "/myplex/account" => Json("""{"MyPlex":{"username":"jampat"}}"""),
                "/library/sections/1/all" => Json(
                    """
                    {"MediaContainer":{"totalSize":1,"Metadata":[
                    {"ratingKey":"101","type":"movie","guid":"plex://movie/5d776b","title":"Sintel","year":2010,"addedAt":1756684800,"viewCount":2,"lastViewedAt":1757530800,"userRating":9,
                     "rating":8.8,"audienceRating":7.2,
                     "Guid":[{"id":"imdb://tt1727587"},{"id":"tmdb://45745"}],
                     "Media":[{"videoResolution":"1080","bitrate":4200,"videoCodec":"h264","Part":[{"file":"/media/Deluno-E2E/Movies/Sintel (2010)/Sintel.mkv"}]}]}]}}
                    """),
                "/library/metadata/101" => Json(
                    """
                    {"MediaContainer":{"Metadata":[{"ratingKey":"101",
                     "Genre":[{"tag":"Animation"}],"Label":[{"tag":"kids"}],"Collection":[{"tag":"Blender films"}],"Role":[{"tag":"Halina Reijn"}],"Director":[{"tag":"Colin Levy"}],
                     "Rating":[{"image":"imdb://image.rating","value":7.4,"type":"audience"},{"image":"rottentomatoes://image.rating.ripe","value":8.8,"type":"critic"},{"image":"rottentomatoes://image.rating.upright","value":7.9,"type":"audience"},{"image":"themoviedb://image.rating","value":7.1,"type":"audience"}]}]}}
                    """),
                "/playlists" => Json("""{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"900","title":"Friday night"}]}}"""),
                "/playlists/900/items" => Json("""{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"101"}]}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value;
        Assert.Equal(["jampat"], library.Users);
        var sintel = Assert.Single(library.Items);
        Assert.Equal("101", sintel.ServerItemId);
        Assert.Equal("45745", sintel.TmdbId);
        Assert.Equal("tt1727587", sintel.ImdbId);
        Assert.Equal("1", sintel.SectionId);
        Assert.Equal("/media/Deluno-E2E/Movies/Sintel (2010)/Sintel.mkv", Assert.Single(sintel.Paths));
        Assert.True(sintel.Watch.PlayedByAnyone);
        Assert.False(sintel.Watch.PerUserKnown);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1757530800), sintel.Watch.LastPlayedUtc);
        var viewer = Assert.Single(sintel.Viewers);
        Assert.Equal(("jampat", true, 2, (bool?)false, 9d), (viewer.User, viewer.Played, viewer.PlayCount, viewer.Favourite, viewer.Rating));
        Assert.Equal(new MediaServerRatings(8.8, 7.2, 7.4, 8.8, 7.9, 7.1), sintel.Ratings);
        Assert.Equal(["Halina Reijn", "Colin Levy"], sintel.People);
        Assert.Equal(["Animation"], sintel.Genres);
        Assert.Equal(["kids"], sintel.Labels);
        Assert.Equal(["Blender films"], sintel.Collections);
        Assert.Equal(["Friday night"], sintel.Playlists);
        Assert.Equal(["jampat"], sintel.WatchlistedBy);
        Assert.Equal(new MediaServerVideo("1080p", 4200, "h264"), sintel.Video);
    }

    [Fact]
    public async Task A_Plex_item_rated_the_maximum_is_read_as_a_favourite()
    {
        var client = new PlexMediaServerClient(Factory(request =>
        {
            if (request.RequestUri!.Host == "discover.provider.plex.tv") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"}]}}"""),
                "/library/sections/1/all" => Json(
                    """{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"101","type":"movie","title":"Sintel","userRating":10}]}}"""),
                "/library/metadata/101" => Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"101"}]}}"""),
                "/playlists" => Json("""{"MediaContainer":{"totalSize":0}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var sintel = Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items);
        var viewer = Assert.Single(sintel.Viewers);
        Assert.True(viewer.Favourite);
    }

    [Fact]
    public async Task A_Plex_item_never_rated_reports_favourite_as_unknown()
    {
        var client = new PlexMediaServerClient(Factory(request =>
        {
            if (request.RequestUri!.Host == "discover.provider.plex.tv") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"}]}}"""),
                "/library/sections/1/all" => Json(
                    """{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"101","type":"movie","title":"Sintel","viewCount":1}]}}"""),
                "/library/metadata/101" => Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"101"}]}}"""),
                "/playlists" => Json("""{"MediaContainer":{"totalSize":0}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var sintel = Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items);
        var viewer = Assert.Single(sintel.Viewers);
        Assert.Null(viewer.Favourite);
    }

    [Fact]
    public async Task A_Plex_watchlist_that_cannot_be_asked_is_unknown_not_empty()
    {
        var client = new PlexMediaServerClient(Factory(request =>
        {
            if (request.RequestUri!.Host == "discover.provider.plex.tv") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"}]}}"""),
                "/library/sections/1/all" => Json("""{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"101","type":"movie","title":"Sintel"}]}}"""),
                "/library/metadata/101" => Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"101"}]}}"""),
                "/playlists" => Json("""{"MediaContainer":{"totalSize":0}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var sintel = Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items);
        Assert.Null(sintel.WatchlistedBy);
    }

    [Fact]
    public async Task Removing_from_Plex_rescans_the_folder_and_empties_trash_and_never_deletes()
    {
        var sent = new List<(HttpMethod Method, string PathAndQuery)>();
        var client = new PlexMediaServerClient(Factory(request =>
        {
            sent.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.PathAndQuery)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var item = new MediaServerItem("101", "movies", "Sintel", 2010, null, null, null, ["/media/Movies/Sintel (2010)/Sintel.mkv"], null, MediaServerWatchState.Unplayed, "1");

        var answer = await client.RemoveAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), item, CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRemoval>.Reported>(answer);
        Assert.Equal((HttpMethod.Get, "/library/sections/1/refresh?path=/media/Movies/Sintel (2010)"), sent[0]);
        Assert.Equal((HttpMethod.Put, "/library/sections/1/emptyTrash"), sent[1]);
        Assert.DoesNotContain(sent, call => call.Method == HttpMethod.Delete);
    }

    /// <summary>
    /// Jellyfin answers a film that is in a collection with the collection,
    /// unless told not to. On the rig a title in "Leaving soon" vanished from
    /// the listing and was read as unwatched (#521). Every listing says not to
    /// group, and a listing that groups anyway is not an answer.
    /// </summary>
    [Fact]
    public async Task Jellyfin_is_asked_not_to_group_films_into_collections_and_a_grouped_listing_is_not_an_answer()
    {
        var queries = new List<string>();
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path == "/Users") return Json("""[{"Id":"u1","Name":"James"}]""");
            if (query.Contains("IncludeItemTypes=Movie", StringComparison.Ordinal)) queries.Add(query);
            if (path == "/Items" && query.Contains("IncludeItemTypes=Movie", StringComparison.Ordinal))
                return Json("""{"Items":[{"Id":"m1","Type":"Movie","Name":"Night of the Living Dead"},{"Id":"bs1","Type":"BoxSet","Name":"Leaving soon"}],"TotalRecordCount":2}""");
            return Json("""{"Items":[],"TotalRecordCount":0}""");
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "movies", CancellationToken.None);

        var unreachable = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(answer);
        Assert.Contains("Leaving soon", unreachable.Reason, StringComparison.Ordinal);
        Assert.NotEmpty(queries);
        Assert.All(queries, query => Assert.Contains("CollapseBoxSetItems=false", query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Plex_section_that_hides_titles_in_collections_is_not_an_answer()
    {
        var client = new PlexMediaServerClient(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"}]}}"""),
                "/myplex/account" => Json("""{"MyPlex":{"username":"james"}}"""),
                "/library/sections/1/all" => Json("""{"MediaContainer":{"size":2,"totalSize":2,"Metadata":[{"ratingKey":"10","type":"movie","title":"Sintel"},{"ratingKey":"99","type":"collection","title":"Leaving soon"}]}}"""),
                _ => Json("""{"MediaContainer":{"size":0,"Metadata":[]}}""")
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        Assert.Contains("Leaving soon", Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(answer).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Plex_refresh_rescans_the_folder_of_a_section_whose_location_matches_the_path()
    {
        var sent = new List<(HttpMethod Method, string PathAndQuery)>();
        var client = new PlexMediaServerClient(Factory(request =>
        {
            sent.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.PathAndQuery)));
            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie","Location":[{"path":"/media/Movies"}]},{"key":"2","type":"show","Location":[{"path":"/media/TV"}]}]}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        }));

        var answer = await client.RefreshAsync(
            Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"),
            "movies",
            [new MediaServerPathChange("/media/Movies/Sintel (2010)/Sintel.mkv", MediaServerChangeKind.Created)],
            CancellationToken.None);

        var refresh = Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value;
        Assert.False(refresh.UsedFallbackScan);
        Assert.Contains((HttpMethod.Get, "/library/sections/1/refresh?path=/media/Movies/Sintel (2010)"), sent);
        Assert.DoesNotContain(sent, call => call.PathAndQuery.StartsWith("/library/sections/2", StringComparison.Ordinal));
        Assert.DoesNotContain(sent, call => call.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task A_Plex_refresh_empties_trash_for_a_deleted_change_but_never_for_created()
    {
        var sent = new List<(HttpMethod Method, string PathAndQuery)>();
        var client = new PlexMediaServerClient(Factory(request =>
        {
            sent.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.PathAndQuery)));
            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie","Location":[{"path":"/media/Movies"}]}]}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        }));

        await client.RefreshAsync(
            Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"),
            "movies",
            [new MediaServerPathChange("/media/Movies/Old (2009)/Old.mkv", MediaServerChangeKind.Deleted)],
            CancellationToken.None);

        Assert.Contains((HttpMethod.Put, "/library/sections/1/emptyTrash"), sent);
    }

    [Fact]
    public async Task A_Plex_refresh_falls_back_to_a_full_scan_when_no_section_maps_the_path()
    {
        var sent = new List<(HttpMethod Method, string PathAndQuery)>();
        var client = new PlexMediaServerClient(Factory(request =>
        {
            sent.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.PathAndQuery)));
            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie","Location":[{"path":"/media/Movies"}]}]}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        }));

        var answer = await client.RefreshAsync(
            Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"),
            "movies",
            [new MediaServerPathChange("/unmapped/Movies/Sintel (2010)/Sintel.mkv", MediaServerChangeKind.Created)],
            CancellationToken.None);

        var refresh = Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value;
        Assert.True(refresh.UsedFallbackScan);
        Assert.Contains((HttpMethod.Get, "/library/sections/1/refresh"), sent);
        Assert.DoesNotContain(sent, call => call.PathAndQuery.Contains("path="));
    }

    [Fact]
    public async Task Jellyfin_and_Emby_refresh_sends_one_call_with_every_changed_path()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            captured = request;
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var answer = await client.RefreshAsync(
            Server(MediaServerKinds.Jellyfin),
            "tv",
            [
                new MediaServerPathChange("/tv/Breaking Bad/Season 01/S01E01.mkv", MediaServerChangeKind.Created),
                new MediaServerPathChange("/tv/Breaking Bad/Season 01/S01E01.old.mkv", MediaServerChangeKind.Deleted)
            ],
            CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer);
        Assert.Equal("/Library/Media/Updated", captured!.RequestUri!.AbsolutePath);
        Assert.Contains("\"Created\"", body);
        Assert.Contains("\"Deleted\"", body);
    }

    [Fact]
    public async Task Jellyfin_falls_back_to_a_full_scan_when_the_targeted_refresh_fails()
    {
        var calls = new List<string>();
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri!.AbsolutePath == "/Library/Media/Updated"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var answer = await client.RefreshAsync(
            Server(MediaServerKinds.Jellyfin),
            "movies",
            [new MediaServerPathChange("/media/Movies/Sintel (2010)/Sintel.mkv", MediaServerChangeKind.Created)],
            CancellationToken.None);

        var refresh = Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value;
        Assert.True(refresh.UsedFallbackScan);
        Assert.Contains("/Library/Refresh", calls);
    }

    [Fact]
    public async Task A_Jellyfin_path_mapping_test_reports_whether_the_server_can_see_the_folder()
    {
        var client = new JellyfinMediaServerClient(Factory(request
            => request.RequestUri!.AbsolutePath == "/Environment/DirectoryContents"
                ? Json("""[{"Path":"/media/Movies/Sintel (2010)","Type":"Directory"}]""")
                : new HttpResponseMessage(HttpStatusCode.NotFound)));

        var answer = await client.TestPathAsync(Server(MediaServerKinds.Jellyfin), "/media/Movies/Sintel (2010)", CancellationToken.None);

        var result = Assert.IsType<MediaServerAnswer<MediaServerPathMappingTest>.Reported>(answer).Value;
        Assert.True(result.Found);
    }

    [Fact]
    public async Task A_Plex_path_mapping_test_reports_the_server_could_not_be_asked()
    {
        var client = new PlexMediaServerClient(Factory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var answer = await client.TestPathAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "/media/Movies", CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerPathMappingTest>.Unreachable>(answer);
    }

    // ------------------------------------------------------ what is playing now (#1012)

    [Fact]
    public async Task Plex_names_the_item_season_and_show_of_every_play()
    {
        var client = new PlexMediaServerClient(Factory(request =>
        {
            Assert.Equal("/status/sessions", request.RequestUri!.AbsolutePath);
            return Json("""{"MediaContainer":{"size":2,"Metadata":[{"ratingKey":"501","type":"movie"},{"ratingKey":"611","parentRatingKey":"61","grandparentRatingKey":"6","type":"episode"}]}}""");
        }));

        var answer = await client.ListPlayingAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None);

        Assert.Equal(["501", "6", "61", "611"], Assert.IsType<MediaServerAnswer<IReadOnlyList<string>>.Reported>(answer).Value.Order());
    }

    [Fact]
    public async Task Plex_with_nothing_playing_says_nothing_is_playing_but_an_unreadable_answer_says_it_could_not_ask()
    {
        var idle = new PlexMediaServerClient(Factory(_ => Json("""{"MediaContainer":{"size":0}}""")));
        Assert.Empty(Assert.IsType<MediaServerAnswer<IReadOnlyList<string>>.Reported>(await idle.ListPlayingAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None)).Value);

        var refused = new PlexMediaServerClient(Factory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.IsType<MediaServerAnswer<IReadOnlyList<string>>.Unreachable>(await refused.ListPlayingAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None));

        var nonsense = new PlexMediaServerClient(Factory(_ => Json("""{"hello":"world"}""")));
        Assert.IsType<MediaServerAnswer<IReadOnlyList<string>>.Unreachable>(await nonsense.ListPlayingAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None));
    }

    [Theory]
    [InlineData(MediaServerKinds.Jellyfin, "/Sessions")]
    [InlineData(MediaServerKinds.Emby, "/emby/Sessions")]
    public async Task Jellyfin_and_Emby_read_the_sessions_that_have_something_playing(string kind, string expectedPath)
    {
        IHttpClientFactory factory = Factory(request =>
        {
            Assert.Equal(expectedPath, request.RequestUri!.AbsolutePath);
            return Json("""
                [{"Id":"s1","UserName":"james"},
                 {"Id":"s2","NowPlayingItem":{"Id":"e9","SeasonId":"se1","SeriesId":"sh1","Type":"Episode"}},
                 {"Id":"s3","NowPlayingItem":{"Id":"m7","Type":"Movie"}}]
                """);
        });
        IMediaServerPlaybackReader client = kind == MediaServerKinds.Jellyfin ? new JellyfinMediaServerClient(factory) : new EmbyMediaServerClient(factory);

        var answer = await client.ListPlayingAsync(Server(kind), CancellationToken.None);

        Assert.Equal(["e9", "m7", "se1", "sh1"], Assert.IsType<MediaServerAnswer<IReadOnlyList<string>>.Reported>(answer).Value.Order());
    }

    [Fact]
    public async Task A_Jellyfin_that_cannot_be_reached_or_answers_nonsense_could_not_be_asked_what_is_playing()
    {
        var down = new JellyfinMediaServerClient(Factory(_ => throw new HttpRequestException("refused")));
        Assert.IsType<MediaServerAnswer<IReadOnlyList<string>>.Unreachable>(await down.ListPlayingAsync(Server(MediaServerKinds.Jellyfin), CancellationToken.None));

        var nonsense = new JellyfinMediaServerClient(Factory(_ => Json("""{"not":"a list"}""")));
        Assert.IsType<MediaServerAnswer<IReadOnlyList<string>>.Unreachable>(await nonsense.ListPlayingAsync(Server(MediaServerKinds.Jellyfin), CancellationToken.None));
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
