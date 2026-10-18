using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A hand send reaches each download client kind the way a person makes it, through the real host: the title page's "Send to
/// downloads" without force, a second send of the same film that asks first (409) and Send as well or Replace, and the Search page's
/// Grab. Every client is the real app's recorded replies over a real loopback port (Transmission 4.1.3, Deluge 2.2.0, SABnzbd 5.1.3,
/// NZBGet 26.3); a client's torrent or job list is the recorded one with the hash, name and id of what was really added, and holds
/// what has been added and not removed, as the real client did. qBittorrent has the same walk in
/// <see cref="TitleSearchAndGrabRoutesAgainstRecordedAppsTests"/>.
/// </summary>
public sealed class AHandSendOnEachClientKindTests
{
    private const string Release = "Sintel.2010.1080p.BluRay.x264-GRP";
    private const string SameFilmAgain = "Sintel.2010.2160p.BluRay.x265-OTHER";

    public static IEnumerable<object[]> Kinds() => [["qbittorrent"], ["transmission"], ["deluge"], ["sabnzbd"], ["nzbget"]];

    /// <summary>One client kind: its recorded app, how to read an add or a removal off what it was sent, and a list that follows both.</summary>
    private abstract class Kind
    {
        public abstract string Protocol { get; }

        public abstract bool Usenet { get; }

        public abstract Task<RecordedAppServer> StartAsync();

        public abstract bool IsAdd(RecordedAppServer.Seen seen);

        public abstract bool IsRemove(RecordedAppServer.Seen seen);

        /// <summary>The hash every torrent release is offered under: the one the client's recorded reply to a magnet add names.</summary>
        public virtual string Hash => "A779FA758F2229586EEBC2E5960EB32FE5659596";

        public string Url(string release) => Usenet
            ? $"http://indexer.invalid/getnzb/{Uri.EscapeDataString(release)}.nzb?apikey=0123456789abcdef"
            : $"magnet:?xt=urn:btih:{Hash}&dn={release}";

        public static Kind For(string protocol) => protocol switch
        {
            "qbittorrent" => new QbittorrentKind(),
            "transmission" => new TransmissionKind(),
            "deluge" => new DelugeKind(),
            "sabnzbd" => new SabnzbdKind(),
            "nzbget" => new NzbgetKind(),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null)
        };
    }

    private sealed class TransmissionKind : Kind
    {
        public override string Protocol => "transmission";

        public override bool Usenet => false;

        public override Task<RecordedAppServer> StartAsync()
        {
            var added = RecordedReplies.Transmission("queued").Find("torrent-add").Reply.Json()["arguments"]!["torrent-added"]!;
            var hash = (string)added["hashString"]!;
            var name = (string)added["name"]!;
            var template = RecordedReplies.Transmission("downloading").Find("torrent-get").Reply.Json()["arguments"]!["torrents"]![0]!;
            var has = false;
            return RecordedAppServer.StartAsync(
                "transmission",
                new[] { "version", "settings", "queued", "removed-with-data" }.Select(RecordedReplies.Transmission),
                (request, body) =>
                {
                    if (body.Contains("\"torrent-add\"", StringComparison.Ordinal)) { has = true; return null; }
                    if (body.Contains("\"torrent-remove\"", StringComparison.Ordinal)) { has = false; return null; }
                    if (!body.Contains("\"torrent-get\"", StringComparison.Ordinal)) return null;
                    var torrents = new JsonArray();
                    if (has)
                    {
                        var one = template.DeepClone();
                        one["hashString"] = hash;
                        one["name"] = name;
                        torrents.Add(one);
                    }

                    var reply = new JsonObject { ["arguments"] = new JsonObject { ["torrents"] = torrents }, ["result"] = "success", ["tag"] = 1 };
                    return RecordedReplies.Json(reply.ToJsonString());
                });
        }

        public override bool IsAdd(RecordedAppServer.Seen seen) => seen.Body.Contains("\"torrent-add\"", StringComparison.Ordinal);

        public override bool IsRemove(RecordedAppServer.Seen seen) => seen.Body.Contains("\"torrent-remove\"", StringComparison.Ordinal);
    }

    private sealed class QbittorrentKind : Kind
    {
        public override string Protocol => "qbittorrent";

        public override bool Usenet => false;

        public override Task<RecordedAppServer> StartAsync()
        {
            var template = RecordedReplies.Qbittorrent("downloading").Find("torrents/info").Reply.Json().AsArray()[0]!;
            var has = false;
            var name = string.Empty;
            return RecordedAppServer.StartAsync(
                "qbittorrent",
                new[] { "sign-in", "version", "settings", "resumed", "removed-with-data", "category-moved", "auto-management-set" }.Select(RecordedReplies.Qbittorrent),
                (request, body) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    if (path.EndsWith("torrents/add", StringComparison.Ordinal))
                    {
                        has = true;
                        name = System.Text.RegularExpressions.Regex.Match(body, "dn=(?<name>[^&]*)").Groups["name"].Value;
                        return null;
                    }

                    if (path.EndsWith("torrents/delete", StringComparison.Ordinal)) { has = false; return null; }
                    if (!path.EndsWith("torrents/info", StringComparison.Ordinal)) return null;
                    var list = new JsonArray();
                    if (has)
                    {
                        var one = template.DeepClone();
                        one["hash"] = Hash.ToLowerInvariant();
                        one["name"] = name;
                        list.Add(one);
                    }

                    return RecordedReplies.Json(list.ToJsonString());
                });
        }

        public override bool IsAdd(RecordedAppServer.Seen seen) => seen.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal);

        public override bool IsRemove(RecordedAppServer.Seen seen) => seen.PathAndQuery.EndsWith("torrents/delete", StringComparison.Ordinal);
    }

    private sealed class DelugeKind : Kind
    {
        public override string Protocol => "deluge";

        public override bool Usenet => false;

        public override string Hash => "5422d19ec8580f2821a8ffa67236cf7ed86fc07d";

        public override Task<RecordedAppServer> StartAsync()
        {
            var listing = RecordedReplies.Deluge("magnet-no-metadata").Find("web.update_ui").Reply;
            var has = false;
            return RecordedAppServer.StartAsync(
                "deluge",
                new[] { "sign-in", "version", "settings", "magnet-no-metadata", "label-set", "removed-with-data" }.Select(RecordedReplies.Deluge),
                (request, body) =>
                {
                    if (body.Contains("core.add_torrent", StringComparison.Ordinal)) { has = true; return null; }
                    if (body.Contains("core.remove_torrent", StringComparison.Ordinal)) { has = false; return null; }
                    if (!body.Contains("web.update_ui", StringComparison.Ordinal)) return null;
                    return has ? listing.ToResponse() : RecordedReplies.Json(listing.BodyWith(("result.torrents", new JsonObject())));
                });
        }

        public override bool IsAdd(RecordedAppServer.Seen seen) => seen.Body.Contains("core.add_torrent", StringComparison.Ordinal);

        public override bool IsRemove(RecordedAppServer.Seen seen) => seen.Body.Contains("core.remove_torrent", StringComparison.Ordinal);
    }

    private sealed class SabnzbdKind : Kind
    {
        public override string Protocol => "sabnzbd";

        public override bool Usenet => true;

        public override Task<RecordedAppServer> StartAsync()
        {
            var withJob = RecordedReplies.Sabnzbd("queued").Find("mode=queue&output").Reply;
            var withoutJob = RecordedReplies.Sabnzbd("removed-without-data").Find("mode=queue&output").Reply;
            var added = RecordedReplies.Sabnzbd("queued").Find("mode=addurl").Reply;
            var has = false;
            return RecordedAppServer.StartAsync(
                "sabnzbd",
                new[] { "version", "settings", "queued", "removed-without-data", "removed-with-data" }.Select(RecordedReplies.Sabnzbd),
                (request, _) =>
                {
                    var query = request.RequestUri!.Query;
                    if (query.Contains("mode=addurl", StringComparison.Ordinal)) { has = true; return added.ToResponse(); }
                    if (query.Contains("mode=queue", StringComparison.Ordinal) && query.Contains("name=delete", StringComparison.Ordinal)) { has = false; return null; }
                    return query.Contains("mode=queue", StringComparison.Ordinal) ? (has ? withJob : withoutJob).ToResponse() : null;
                });
        }

        public override bool IsAdd(RecordedAppServer.Seen seen) => seen.PathAndQuery.Contains("mode=addurl", StringComparison.Ordinal);

        public override bool IsRemove(RecordedAppServer.Seen seen) => seen.PathAndQuery.Contains("mode=queue", StringComparison.Ordinal) && seen.PathAndQuery.Contains("name=delete", StringComparison.Ordinal);
    }

    private sealed class NzbgetKind : Kind
    {
        public override string Protocol => "nzbget";

        public override bool Usenet => true;

        public override Task<RecordedAppServer> StartAsync()
        {
            var withJob = RecordedReplies.Nzbget("queued").Find("listgroups").Reply;
            var withoutJob = RecordedReplies.Nzbget("removed-without-data").Find("listgroups").Reply;
            var has = false;
            return RecordedAppServer.StartAsync(
                "nzbget",
                new[] { "version", "settings", "queued", "removed-without-data" }.Select(RecordedReplies.Nzbget),
                (request, body) =>
                {
                    if (body.Contains("\"append\"", StringComparison.Ordinal)) { has = true; return null; }
                    if (body.Contains("\"editqueue\"", StringComparison.Ordinal) && body.Contains("GroupFinalDelete", StringComparison.Ordinal)) { has = false; return null; }
                    return body.Contains("\"listgroups\"", StringComparison.Ordinal) ? (has ? withJob : withoutJob).ToResponse() : null;
                });
        }

        public override bool IsAdd(RecordedAppServer.Seen seen) => seen.Body.Contains("\"append\"", StringComparison.Ordinal);

        public override bool IsRemove(RecordedAppServer.Seen seen) => seen.Body.Contains("\"editqueue\"", StringComparison.Ordinal) && seen.Body.Contains("Delete", StringComparison.Ordinal);
    }

    private sealed class Rig(Kind kind, RecordedAppServer client, ApplicationTestHost app, string libraryId, string clientId) : IAsyncDisposable
    {
        public Kind Kind { get; } = kind;

        public RecordedAppServer Client { get; } = client;

        public ApplicationTestHost App { get; } = app;

        public string LibraryId { get; } = libraryId;

        public string ClientId { get; } = clientId;

        public int Adds => Client.Requests.Count(Kind.IsAdd);

        public int Removals => Client.Requests.Count(Kind.IsRemove);

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Client.DisposeAsync();
        }
    }

    private static async Task<JsonElement> PostOkAsync(ApplicationTestHost app, string url, object? body = null)
    {
        var response = body is null ? await app.Client.PostAsync(url, content: null) : await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Rig> RigAsync(string protocol)
    {
        var kind = Kind.For(protocol);
        var client = await kind.StartAsync();
        var app = await ApplicationTestHost.StartAsync();
        var created = await PostOkAsync(app, "/api/download-clients", new
        {
            protocol, host = client.Host, port = client.Port, username = protocol is "sabnzbd" ? null : "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        var clientId = created.GetProperty("id").GetString()!;
        var tested = await app.Client.PostAsync($"/api/download-clients/{clientId}/test", content: null);
        Assert.Equal("healthy", (await tested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = clientId, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        return new Rig(kind, client, app, libraryId, clientId);
    }

    private static async Task<string> AddFilmAsync(Rig rig, string title, int year)
        => (await PostOkAsync(rig.App, "/api/movies/", new { title, releaseYear = year, monitored = true, libraryId = rig.LibraryId, searchForMissingNow = false })).GetProperty("id").GetString()!;

    private static object Pick(Rig rig, string release, string? duplicateAction = null)
        => new { releaseName = release, indexerName = "Fixture", downloadUrl = rig.Kind.Url(release), candidateQuality = "Bluray 1080p", sizeBytes = 8_000_000_000L, duplicateAction };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_release_picked_by_hand_without_force_is_sent_to_the_clients_own_add_and_recorded_as_a_send(string protocol)
    {
        await using var rig = await RigAsync(protocol);
        var film = await AddFilmAsync(rig, "Sintel", 2010);

        var sent = await PostOkAsync(rig.App, $"/api/movies/{film}/grab", Pick(rig, Release));

        Assert.False(sent.GetProperty("forceOverride").GetBoolean());
        Assert.Equal("sent", sent.GetProperty("dispatchStatus").GetString());
        Assert.Equal(1, rig.Adds);
        var add = Assert.Single(rig.Client.Requests, rig.Kind.IsAdd);
        Assert.Contains(rig.Kind.Usenet ? "getnzb" : rig.Kind.Hash, add.Body + add.PathAndQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT COUNT(*) FROM download_dispatches WHERE entity_id = '{film}' AND status = 'sent';"));
        Assert.Empty(rig.Client.Unanswered);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_second_send_of_a_film_still_downloading_asks_first_and_Send_as_well_keeps_both_and_Replace_takes_the_first_out_of_the_client(string protocol)
    {
        await using var rig = await RigAsync(protocol);
        var film = await AddFilmAsync(rig, "Sintel", 2010);
        await PostOkAsync(rig.App, $"/api/movies/{film}/grab", Pick(rig, Release));
        Assert.Equal(1, rig.Adds);

        var asked = await rig.App.Client.PostAsJsonAsync($"/api/movies/{film}/grab", Pick(rig, SameFilmAgain));

        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        var question = await asked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already-downloading", question.GetProperty("code").GetString());
        Assert.Equal(Release, question.GetProperty("releaseName").GetString());
        Assert.Equal(1, rig.Adds);
        Assert.Equal(0, rig.Removals);

        await PostOkAsync(rig.App, $"/api/movies/{film}/grab", Pick(rig, SameFilmAgain, "send-as-well"));
        Assert.Equal(2, rig.Adds);
        Assert.Equal(0, rig.Removals);

        var other = await AddFilmAsync(rig, "Tears of Steel", 2012);
        await PostOkAsync(rig.App, $"/api/movies/{other}/grab", Pick(rig, "Tears.of.Steel.2012.1080p.BluRay.x264-GRP"));
        Assert.Equal(3, rig.Adds);
        await PostOkAsync(rig.App, $"/api/movies/{other}/grab", Pick(rig, "Tears.of.Steel.2012.2160p.BluRay.x265-OTHER", "replace"));
        Assert.Equal(1, rig.Removals);
        Assert.Equal(4, rig.Adds);
        Assert.Empty(rig.Client.Unanswered);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task The_Search_page_grab_adds_the_release_to_the_clients_own_add_and_answers_that_it_succeeded(string protocol)
    {
        await using var rig = await RigAsync(protocol);

        var grab = await PostOkAsync(rig.App, "/api/search/grab", new
        {
            releaseName = Release, downloadUrl = rig.Kind.Url(Release), indexerId = "fixture-indexer", indexerName = "Fixture", searchType = "movie", sizeBytes = 8_000_000_000L
        });

        Assert.True(grab.GetProperty("succeeded").GetBoolean(), grab.ToString());
        Assert.Equal(1, rig.Adds);
        var add = Assert.Single(rig.Client.Requests, rig.Kind.IsAdd);
        Assert.Contains(rig.Kind.Usenet ? "getnzb" : rig.Kind.Hash, add.Body + add.PathAndQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Client.Unanswered);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_release_picked_by_hand_for_an_episode_is_sent_to_the_clients_own_add_and_a_second_pick_of_that_episode_asks_first_while_another_does_not(string protocol)
    {
        await using var rig = await RigAsync(protocol);
        var showsLibrary = await ApiScenes.CreateLibraryAsync(rig.App, "tv", "Shows", "Shows");
        var routed = await rig.App.Client.PutAsJsonAsync($"/api/libraries/{showsLibrary}/routing", new
        {
            sources = Array.Empty<object>(),
            downloadClients = new[] { new { downloadClientId = rig.ClientId, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        var created = await PostOkAsync(rig.App, "/api/series/", new CreateSeriesRequest("The Simpsons", 1989, "tt0096697", LibraryId: showsLibrary));
        var seriesId = created.GetProperty("id").GetString()!;
        await rig.App.Services.GetRequiredService<ISeriesCatalogRepository>().SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(0, 78, "Extreme Makeover Homer Edition", null, DateTimeOffset.Parse("2026-10-01T00:00:00Z")),
                new CatalogueEpisodeItem(0, 79, "Another Special", null, DateTimeOffset.Parse("2026-10-02T00:00:00Z"))
            ],
            "tmdb",
            CancellationToken.None);
        const string Episode = "The.Simpsons.S00E78.Extreme.Makeover.Homer.Edition.2026.1080p.DSNP.WEB-DL-GRP";
        const string EpisodeAgain = "The.Simpsons.S00E78.Extreme.Makeover.Homer.Edition.2026.2160p.WEB-DL-OTHER";
        const string NextEpisode = "The.Simpsons.S00E79.Another.Special.2026.1080p.WEB-DL-GRP";
        object Pick(string release, string? duplicateAction = null)
            => new { releaseName = release, indexerName = "Fixture", downloadUrl = rig.Kind.Url(release), candidateQuality = "WEB 1080p", sizeBytes = 2_000_000_000L, duplicateAction };

        var sent = await PostOkAsync(rig.App, $"/api/series/{seriesId}/grab", Pick(Episode));

        Assert.Equal("sent", sent.GetProperty("dispatchStatus").GetString());
        Assert.Equal(1, rig.Adds);
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT COUNT(*) FROM download_dispatches WHERE entity_id = '{seriesId}' AND status = 'sent';"));
        var asked = await rig.App.Client.PostAsJsonAsync($"/api/series/{seriesId}/grab", Pick(EpisodeAgain));
        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        Assert.Equal("already-downloading", (await asked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(1, rig.Adds);
        await PostOkAsync(rig.App, $"/api/series/{seriesId}/grab", Pick(NextEpisode));
        Assert.Equal(2, rig.Adds);
        await PostOkAsync(rig.App, $"/api/series/{seriesId}/grab", Pick(EpisodeAgain, "replace"));
        Assert.Equal(1, rig.Removals);
        Assert.Equal(3, rig.Adds);
        Assert.Empty(rig.Client.Unanswered);
    }
}
