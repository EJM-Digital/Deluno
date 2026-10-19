using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Only take releases naming", "Never take releases naming" and the wait before taking a torrent on a quality profile, judged on
/// Jackett 0.24.2850's recorded YTS reply for Sintel (2010) over the real route and databases. Nothing is sent: the search runs
/// in preview, so each test reads the decision the profile gives the one recorded release.
/// </summary>
public sealed class AProfilesReleaseWordsDecideOnRecordedIndexerResultsTests
{
    private const string JackettPath = "/api/v2.0/indexers/yts/results/torznab/api";
    private const string RecordedRelease = "Sintel (2010) 720p BRRip x264 -YTS";

    private static async Task<JsonElement> PreviewWithAsync(object acquisition)
    {
        await using var indexer = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-movie-imdb"), RecordedReplies.Jackett("search-text"));
        await using var app = await ApplicationTestHost.StartAsync();
        var filmId = await SeedAsync(app, indexer.BaseUrl + JackettPath, acquisition);
        using var response = await app.Client.PostAsync($"/api/movies/{filmId}/search?mode=preview", content: null);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var preview = JsonSerializer.Deserialize<JsonElement>(raw);
        return preview.GetProperty("candidates").EnumerateArray().Single().Clone();
    }

    [Fact]
    public async Task A_release_naming_a_word_the_profile_never_takes_is_refused_with_that_word_in_the_reason()
    {
        var candidate = await PreviewWithAsync(new { mustNotContain = "BRRip, CAM" });

        Assert.Equal(RecordedRelease, candidate.GetProperty("releaseName").GetString());
        Assert.Equal("rejected", candidate.GetProperty("decisionStatus").GetString(), ignoreCase: true);
        Assert.Contains(candidate.GetProperty("decisionReasons").EnumerateArray().Select(reason => reason.GetString()), reason => reason!.Contains("BRRip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_release_missing_every_word_the_profile_insists_on_is_refused_with_the_missing_word_in_the_reason()
    {
        var missing = await PreviewWithAsync(new { mustContain = "NTb, FLUX" });

        Assert.Equal("rejected", missing.GetProperty("decisionStatus").GetString(), ignoreCase: true);
        Assert.Contains(missing.GetProperty("decisionReasons").EnumerateArray().Select(reason => reason.GetString()), reason => reason!.Contains("NTb", StringComparison.Ordinal));
    }

    [Fact(Skip = "DEFECT: the Quality Profile step 5 box says any one of the words must appear in the release name, but the engine requires every word (ReleaseDecisionEngine refuses at the first missing required term). Recorded YTS release \"Sintel (2010) 720p BRRip x264 -YTS\" with mustContain \"NTb, YTS\" is rejected with \"Release name is missing required term 'NTb'.\"")]
    public async Task A_release_naming_any_one_of_the_words_the_profile_insists_on_is_taken()
    {
        var named = await PreviewWithAsync(new { mustContain = "NTb, YTS" });
        Assert.NotEqual("rejected", named.GetProperty("decisionStatus").GetString(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_pattern_between_slashes_works_in_both_lists_ignoring_case_when_asked_to()
    {
        var wanted = await PreviewWithAsync(new { mustContain = "/-yts$/i" });
        Assert.NotEqual("rejected", wanted.GetProperty("decisionStatus").GetString(), StringComparer.OrdinalIgnoreCase);

        var unwanted = await PreviewWithAsync(new { mustNotContain = "/x26[45]/i" });
        Assert.Equal("rejected", unwanted.GetProperty("decisionStatus").GetString(), ignoreCase: true);
    }

    [Fact]
    public async Task A_wait_before_taking_a_torrent_holds_a_torrent_release_that_has_only_just_appeared()
    {
        var held = await PreviewWithAsync(new { torrentDelayMinutes = 600 });

        Assert.NotEqual("accepted", held.GetProperty("decisionStatus").GetString(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(held.GetProperty("decisionReasons").EnumerateArray().Select(reason => reason.GetString()), reason => reason!.Contains("wait", StringComparison.OrdinalIgnoreCase) || reason.Contains("delay", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> SeedAsync(ApplicationTestHost app, string indexerUrl, object acquisition)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var client = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest("Fake", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None);
        var indexer = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Jackett YTS", "torznab", "public", indexerUrl, "k-secret-1", 10, "2000", "", "movies", true),
            CancellationToken.None);
        await connections.UpdateIndexerHealthAsync(indexer.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = new[] { new { indexerId = indexer.Id, priority = 1 } },
            downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        // A profile that accepts the small 720p release, so the words and the wait are the only things that can change the decision.
        var profileResponse = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "Small 720p films",
            mediaType = "movies",
            cutoffQuality = "Bluray 720p",
            allowedQualities = "Bluray 720p",
            customFormatIds = string.Empty,
            upgradeUntilCutoff = false,
            upgradeUnknownItems = false,
            sizeRules = new[] { new { quality = "Bluray 720p", minGb = 0.0, maxGb = 0.0, minMb = 0.0, maxMb = 0.0 } },
            acquisition
        });
        Assert.True(profileResponse.IsSuccessStatusCode, await profileResponse.Content.ReadAsStringAsync());
        var qualityProfileId = (await profileResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId, searchForMissingNow = false, qualityProfileId });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
}
