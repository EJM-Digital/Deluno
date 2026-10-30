using System.Net;
using System.Net.Http.Json;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1411: choosing "Both" when adding a library makes two linked libraries, one in the Movies engine and one in
/// the TV engine, sharing one name and one root (Movies and TV subfolders). Each is an ordinary library of its type.
/// </summary>
public sealed class ALibraryPairTests
{
    [Fact]
    public async Task Both_makes_a_movies_library_and_a_tv_library_under_one_root_and_links_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Anime")).FullName;

        var response = await app.Client.PostAsJsonAsync("/api/libraries/pair", new { name = "Anime", rootPath = root });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var pair = (await response.Content.ReadFromJsonAsync<LibraryItem[]>())!;
        var movies = Assert.Single(pair, item => item.MediaType == "movies");
        var tv = Assert.Single(pair, item => item.MediaType == "tv");
        Assert.Equal("Anime Movies", movies.Name);
        Assert.Equal("Anime TV", tv.Name);
        Assert.Equal(Path.Combine(root, "Movies"), movies.RootPath);
        Assert.Equal(Path.Combine(root, "TV"), tv.RootPath);
        Assert.True(Directory.Exists(movies.RootPath));
        Assert.True(Directory.Exists(tv.RootPath));
        Assert.False(string.IsNullOrEmpty(movies.PairId));
        Assert.Equal(movies.PairId, tv.PairId);

        var listed = (await app.Client.GetFromJsonAsync<LibraryItem[]>("/api/libraries"))!;
        var listedMovies = listed.Single(item => item.Id == movies.Id);
        var listedTv = listed.Single(item => item.Id == tv.Id);
        Assert.Equal(tv.Id, listedMovies.PairedLibraryId);
        Assert.Equal("Anime TV", listedMovies.PairedLibraryName);
        Assert.Equal(movies.Id, listedTv.PairedLibraryId);
        Assert.Equal("Anime Movies", listedTv.PairedLibraryName);
    }

    [Fact]
    public async Task Each_library_of_the_pair_takes_its_own_types_quality_profile()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Anime")).FullName;
        var tiers = (await app.Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/quality-model")).GetProperty("tiers")
            .EnumerateArray().Select(tier => tier.GetProperty("name").GetString()!).ToArray();
        var profiles = new Dictionary<string, string>();
        foreach (var type in new[] { "movies", "tv" })
        {
            using var created = await app.Client.PostAsJsonAsync("/api/quality-profiles", new { name = "Profile " + type, mediaType = type, cutoffQuality = tiers[0], allowedQualities = string.Join(",", tiers), customFormatIds = string.Empty, upgradeUntilCutoff = true });
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            profiles[type] = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString()!;
        }

        var response = await app.Client.PostAsJsonAsync("/api/libraries/pair", new { name = "Anime", rootPath = root, moviesQualityProfileId = profiles["movies"], tvQualityProfileId = profiles["tv"] });
        var made = (await response.Content.ReadFromJsonAsync<LibraryItem[]>())!;
        var pair = (await app.Client.GetFromJsonAsync<LibraryItem[]>("/api/libraries"))!
            .Where(item => made.Any(m => m.Id == item.Id)).ToArray();

        foreach (var library in pair)
        {
            Assert.False(string.IsNullOrEmpty(library.QualityProfileId));
        }

        var movies = pair.Single(item => item.MediaType == "movies");
        var tv = pair.Single(item => item.MediaType == "tv");
        Assert.Equal(profiles["movies"], movies.QualityProfileId);
        Assert.Equal(profiles["tv"], tv.QualityProfileId);
    }

    [Fact]
    public async Task A_pair_without_a_name_or_an_existing_root_is_refused_and_nothing_is_made()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var noName = await app.Client.PostAsJsonAsync("/api/libraries/pair", new { name = "", rootPath = app.DataRoot });
        var noRoot = await app.Client.PostAsJsonAsync("/api/libraries/pair", new { name = "Anime", rootPath = Path.Combine(app.DataRoot, "missing") });

        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noRoot.StatusCode);
        var listed = (await app.Client.GetFromJsonAsync<LibraryItem[]>("/api/libraries"))!;
        Assert.DoesNotContain(listed, item => item.Name.StartsWith("Anime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleting_one_of_the_pair_keeps_the_other_and_it_stops_being_a_pair()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (movies, tv) = await MakePairAsync(app);

        var deleted = await app.Client.DeleteAsync($"/api/libraries/{movies.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var listed = (await app.Client.GetFromJsonAsync<LibraryItem[]>("/api/libraries"))!;
        var left = Assert.Single(listed, item => item.Id == tv.Id);
        Assert.Null(left.PairedLibraryId);
        Assert.Null(left.PairId);
        Assert.DoesNotContain(listed, item => item.Id == movies.Id);
    }

    [Fact]
    public async Task Deleting_the_pair_removes_both_libraries()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (movies, tv) = await MakePairAsync(app);

        var deleted = await app.Client.DeleteAsync($"/api/libraries/{movies.Id}?pair=true");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var listed = (await app.Client.GetFromJsonAsync<LibraryItem[]>("/api/libraries"))!;
        Assert.DoesNotContain(listed, item => item.Id == movies.Id || item.Id == tv.Id);
    }

    [Fact]
    public async Task Deleting_the_pair_of_a_library_that_has_no_partner_removes_only_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var made = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root });
        var films = (await made.Content.ReadFromJsonAsync<LibraryItem>())!;

        var deleted = await app.Client.DeleteAsync($"/api/libraries/{films.Id}?pair=true");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    private static async Task<(LibraryItem Movies, LibraryItem Tv)> MakePairAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Anime")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries/pair", new { name = "Anime", rootPath = root });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var pair = (await response.Content.ReadFromJsonAsync<LibraryItem[]>())!;
        return (pair.Single(item => item.MediaType == "movies"), pair.Single(item => item.MediaType == "tv"));
    }
}
