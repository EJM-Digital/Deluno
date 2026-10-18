using System.Net;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// GET /api/activity as the full history (#1100): every filter on its own and combined, the
/// turned-down releases written once from where they are turned down, and paging.
/// </summary>
public sealed class ActivityHistoryApiTests
{
    [Fact]
    public void A_batch_rename_s_old_and_new_file_paths_are_read_from_each_title_and_episode()
    {
        var details = "{\"renamed\":[{\"seriesId\":\"s1\",\"Title\":\"Show\",\"from\":\"Old\",\"to\":\"New\",\"oldPath\":\"/tv/Old\",\"newPath\":\"/tv/New\",\"files\":[{\"oldPath\":\"/tv/Old/S01E01.mkv\",\"newPath\":\"/tv/New/S01E01.mkv\"}]}],\"failures\":[]}";

        var facts = ActivityHistory.FactsFromDetails(details);

        Assert.Contains("/tv/Old/S01E01.mkv", facts.FilePaths);
        Assert.Contains("/tv/New/S01E01.mkv", facts.FilePaths);
    }

    private static async Task<JsonElement[]> ListAsync(ApplicationTestHost app, string query = "")
    {
        var response = await app.Client.GetAsync("/api/activity?pageSize=200" + query);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static string[] Messages(IEnumerable<JsonElement> items) => items.Select(item => item.GetProperty("message").GetString()!).ToArray();

    private static async Task SeedAsync(ApplicationTestHost app)
    {
        var feed = app.Services.GetRequiredService<IActivityFeedRepository>();
        await feed.RecordActivityAsync("movie.search.manual", "Searched for Sintel by hand.", null, null, "movie", "m1", CancellationToken.None);
        await feed.RecordActivityAsync("movie.release.grabbed", "Sintel.2010.1080p was chosen for Sintel and sent to qBit.",
            """{"title":"Sintel","indexerName":"NZBgeek","releaseName":"Sintel.2010.1080p"}""", null, "movie", "m1", CancellationToken.None);
        await feed.RecordActivityAsync("download.client.failed", "qBit reported a failed download for Moon.", """{"title":"Moon","clientName":"qBit"}""", null, "movie", "m2", CancellationToken.None);
        await feed.RecordActivityAsync("filesystem.import.completed", "Imported Sintel.",
            """{"title":"Sintel","sourcePath":"/downloads/Sintel.2010.mkv","destinationPath":"/movies/Sintel (2010)/Sintel (2010).mkv"}""", null, "movie", "m1", CancellationToken.None);
        await feed.RecordActivityAsync("processing.completed", "Weir finished Sintel.", """{"title":"Sintel","processorName":"Weir"}""", null, "movie", "m1", CancellationToken.None);
        await feed.RecordActivityAsync("subtitle.fetched", "Fetched a en subtitle for Moon from OpenSubtitles.",
            """{"title":"Moon","writtenPath":"/movies/Moon (2009)/Moon (2009).en.srt","providerName":"OpenSubtitles"}""", null, "subtitle-provider", "p1", CancellationToken.None);
        await feed.RecordActivityAsync("release.blocked", "You blocked \"Fake.Film\"; every indexer's copy is refused from now on.", null, null, "movie", "m3", CancellationToken.None);
        await feed.RecordActivityAsync("recycle.restored", "Restored Moon.mkv from the recycle bin.", """{"path":"/movies/Moon (2009)/Moon.mkv"}""", null, "recycle-bin", "r1", CancellationToken.None);
    }

    [Fact]
    public async Task Each_kind_of_event_is_found_under_its_own_type()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app);

        Assert.Equal(["Searched for Sintel by hand."], Messages(await ListAsync(app, "&type=search")));
        Assert.Single(await ListAsync(app, "&type=grabbed"));
        Assert.Equal(["qBit reported a failed download for Moon."], Messages(await ListAsync(app, "&type=failed")));
        Assert.Equal(["Imported Sintel."], Messages(await ListAsync(app, "&type=imported")));
        Assert.Equal(["Fetched a en subtitle for Moon from OpenSubtitles."], Messages(await ListAsync(app, "&type=subtitles")));
        Assert.Single(await ListAsync(app, "&type=blocklisted"));
        Assert.Equal(["Restored Moon.mkv from the recycle bin."], Messages(await ListAsync(app, "&type=restored")));
        Assert.Equal(8, (await ListAsync(app)).Length);
    }

    [Fact]
    public async Task Title_file_source_and_outcome_filters_each_narrow_the_list()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app);

        // A title is found by the title an event carries, or by its sentence.
        Assert.Equal(4, (await ListAsync(app, "&title=sintel")).Length);
        Assert.Equal(3, (await ListAsync(app, "&title=Moon")).Length);

        // A file is found by either of its paths.
        Assert.Equal(["Imported Sintel."], Messages(await ListAsync(app, "&file=%2Fdownloads%2FSintel.2010.mkv")));
        Assert.Equal(["Imported Sintel."], Messages(await ListAsync(app, "&file=Sintel%20(2010).mkv")));

        // Source kinds: indexers, clients, processors, other connections.
        var indexer = Messages(await ListAsync(app, "&source=indexer"));
        Assert.Contains("Searched for Sintel by hand.", indexer);
        Assert.Contains(indexer, message => message.StartsWith("Sintel.2010.1080p was chosen", StringComparison.Ordinal));
        Assert.Equal(["qBit reported a failed download for Moon."], Messages(await ListAsync(app, "&source=client")));
        Assert.Equal(["Weir finished Sintel."], Messages(await ListAsync(app, "&source=processor")));
        Assert.Equal(["Fetched a en subtitle for Moon from OpenSubtitles."], Messages(await ListAsync(app, "&source=connection")));
        Assert.Equal(["Weir finished Sintel."], Messages(await ListAsync(app, "&sourceName=weir")));

        // Outcomes.
        Assert.Equal(["qBit reported a failed download for Moon."], Messages(await ListAsync(app, "&outcome=problem")));
        Assert.Equal(7, (await ListAsync(app, "&outcome=good")).Length);

        // Filters combine.
        Assert.Equal(["Imported Sintel."], Messages(await ListAsync(app, "&title=Sintel&type=imported")));
        Assert.Empty(await ListAsync(app, "&title=Moon&type=imported"));
    }

    [Fact]
    public async Task The_title_page_link_finds_everything_about_that_title_by_its_id()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app);

        var items = await ListAsync(app, "&relatedEntityType=movie&relatedEntityId=m1");

        Assert.Equal(4, items.Length);
        Assert.All(items, item => Assert.Equal("m1", item.GetProperty("relatedEntityId").GetString()));
    }

    [Fact]
    public async Task The_date_range_keeps_only_events_inside_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var feed = app.Services.GetRequiredService<IActivityFeedRepository>();
        await feed.RecordActivityBatchAsync(
        [
            new Deluno.Jobs.Contracts.ActivityEventItem("a1", "filesystem.import.completed", "Imported Old.", null, null, null, null, DateTimeOffset.Parse("2026-09-01T10:00:00+00:00")),
            new Deluno.Jobs.Contracts.ActivityEventItem("a2", "filesystem.import.completed", "Imported Middle.", null, null, null, null, DateTimeOffset.Parse("2026-09-15T10:00:00+00:00")),
            new Deluno.Jobs.Contracts.ActivityEventItem("a3", "filesystem.import.completed", "Imported New.", null, null, null, null, DateTimeOffset.Parse("2026-09-30T10:00:00+00:00"))
        ], CancellationToken.None);

        Assert.Equal(["Imported New.", "Imported Middle."], Messages(await ListAsync(app, "&since=2026-09-10T00:00:00Z")));
        Assert.Equal(["Imported Middle."], Messages(await ListAsync(app, "&since=2026-09-10T00:00:00Z&until=2026-09-20T00:00:00Z")));
        Assert.Equal(["Imported Middle.", "Imported Old."], Messages(await ListAsync(app, "&until=2026-09-20T00:00:00Z")));
    }

    [Fact]
    public async Task A_turned_down_release_is_written_once_with_its_title_release_indexer_and_reason()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var recorder = app.Services.GetRequiredService<ITurnedDownReleaseRecorder>();
        await recorder.RecordAsync("Sintel", "movie", "m1",
        [
            new TurnedDownRelease("Sintel.2010.GERMAN.1080p", "language", "audio.language is German", "NZBgeek"),
            new TurnedDownRelease("Sintel.2010.2160p", "other", "Bluray 2160p is not one of the qualities this profile allows (Bluray 1080p).", "Idx")
        ], CancellationToken.None);
        // The same search again an instant later adds nothing.
        await recorder.RecordAsync("Sintel", "movie", "m1", [new TurnedDownRelease("Sintel.2010.GERMAN.1080p", "language", "audio.language is German", "NZBgeek")], CancellationToken.None);

        var items = await ListAsync(app, "&type=turned-down");

        Assert.Equal(2, items.Length);
        var german = items.Single(item => item.GetProperty("message").GetString()!.Contains("GERMAN", StringComparison.Ordinal));
        Assert.Equal("Turned down Sintel.2010.GERMAN.1080p · Not in a language you want", german.GetProperty("message").GetString());
        Assert.Equal("Sintel", german.GetProperty("title").GetString());
        Assert.Equal("NZBgeek", german.GetProperty("sourceName").GetString());
        Assert.Equal("language", german.GetProperty("reasonGroup").GetString());
        Assert.Equal("movie", german.GetProperty("relatedEntityType").GetString());
        Assert.Equal("m1", german.GetProperty("relatedEntityId").GetString());
        Assert.Equal("indexer", german.GetProperty("source").GetString());
        Assert.Equal("turned-down", german.GetProperty("type").GetString());
        // A row written as "other" is worded from its raw reason.
        Assert.Equal("notQuality", items.Single(item => item.GetProperty("message").GetString()!.Contains("2160p", StringComparison.Ordinal)).GetProperty("reasonGroup").GetString());
    }

    [Fact]
    public async Task Turned_down_releases_stay_out_of_the_plain_list_until_asked_for_and_filter_by_reason_and_day()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedAsync(app);
        var recorder = app.Services.GetRequiredService<ITurnedDownReleaseRecorder>();
        await recorder.RecordAsync("Sintel", "movie", "m1",
        [
            new TurnedDownRelease("Cam.Sintel", "fake", "CAM", "Idx"),
            new TurnedDownRelease("Dub.Sintel", "language", "audio.language", "Idx")
        ], CancellationToken.None);

        // The plain newest-first list (what the bell and Live read) is not flooded.
        Assert.Equal(8, (await ListAsync(app)).Length);
        // Asking for a title brings them in, so a title's history is complete.
        Assert.Equal(6, (await ListAsync(app, "&title=Sintel")).Length);
        Assert.Equal(["Turned down Dub.Sintel · Not in a language you want"], Messages(await ListAsync(app, "&type=turned-down&reason=language")));
        Assert.Equal(["Turned down Cam.Sintel · Fake or mislabelled"], Messages(await ListAsync(app, "&reason=fake")));
        var today = DateTimeOffset.UtcNow.Date.AddDays(-1).ToString("O");
        Assert.Equal(2, (await ListAsync(app, $"&type=turned-down&since={Uri.EscapeDataString(today)}")).Length);
        var tomorrow = DateTimeOffset.UtcNow.Date.AddDays(2).ToString("O");
        Assert.Empty(await ListAsync(app, $"&type=turned-down&since={Uri.EscapeDataString(tomorrow)}"));
    }

    [Fact]
    public async Task Paging_walks_a_filtered_history_without_repeats_or_gaps()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var feed = app.Services.GetRequiredService<IActivityFeedRepository>();
        var at = DateTimeOffset.Parse("2026-09-30T10:00:00+00:00");
        await feed.RecordActivityBatchAsync(
            Enumerable.Range(0, 25).Select(i => new Deluno.Jobs.Contracts.ActivityEventItem(
                $"id{i:D2}", "filesystem.import.completed", $"Imported {i:D2}.", null, null, null, null, at.AddMinutes(i / 2))).ToArray(),
            CancellationToken.None);

        var seen = new List<string>();
        string? token = null;
        do
        {
            var response = await app.Client.GetAsync("/api/activity?pageSize=10&type=imported" + (token is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(token)));
            var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            seen.AddRange(root.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
            token = root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (token is not null);

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
    }

    // The Activity list's column sort (#1119): the server sorts before it pages, so the whole history is in order.
    private static async Task SeedForSortAsync(ApplicationTestHost app)
    {
        var feed = app.Services.GetRequiredService<IActivityFeedRepository>();
        var at = DateTimeOffset.Parse("2026-09-30T10:00:00+00:00");
        string[] titles = ["Sintel", "moon", "Arrival", "Brazil", "sintel", null!, "Zodiac"];
        string[] kinds = ["filesystem.import.completed", "movie.release.grabbed", "download.client.failed"];
        await feed.RecordActivityBatchAsync(
            Enumerable.Range(0, 31).Select(i =>
            {
                var title = titles[i % titles.Length];
                return new Deluno.Jobs.Contracts.ActivityEventItem(
                    $"id{i:D2}", kinds[i % kinds.Length], $"Step {(i * 7) % 10} for {title ?? "nothing"}.",
                    title is null ? null : $$"""{"title":"{{title}}"}""", null, null, null,
                    // Pairs share a minute, so the ties are real.
                    at.AddMinutes(i / 2));
            }).ToArray(),
            CancellationToken.None);
    }

    private static async Task<List<JsonElement>> WalkAsync(ApplicationTestHost app, string query, int pageSize, int maxPages = 20)
    {
        var all = new List<JsonElement>();
        string? token = null;
        var pages = 0;
        do
        {
            var response = await app.Client.GetAsync($"/api/activity?pageSize={pageSize}{query}" + (token is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(token)));
            var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            all.AddRange(root.GetProperty("items").EnumerateArray().Select(item => item.Clone()));
            token = root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            pages++;
        }
        while (token is not null && pages < maxPages);
        Assert.True(pages >= 2, "The test needs at least two pages.");
        return all;
    }

    private static string TitleOf(JsonElement item)
        => item.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString()! : item.GetProperty("label").GetString()!;

    [Theory]
    [InlineData("title", "asc")]
    [InlineData("title", "desc")]
    [InlineData("what", "asc")]
    [InlineData("what", "desc")]
    [InlineData("when", "asc")]
    [InlineData("when", "desc")]
    public async Task A_sort_orders_the_whole_history_across_pages_without_repeats_or_gaps(string sortBy, string sortDir)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedForSortAsync(app);

        var all = await WalkAsync(app, $"&sortBy={sortBy}&sortDir={sortDir}", pageSize: 8);

        var ids = all.Select(item => item.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(31, ids.Length);
        Assert.Equal(31, ids.Distinct().Count());

        // Ties keep newest first (and, on a time sort, the id decides), so the whole order is known.
        var everything = (await ListAsync(app)).ToArray();
        var newestFirst = everything.OrderByDescending(item => item.GetProperty("createdUtc").GetDateTimeOffset()).ThenByDescending(item => item.GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
        var desc = sortDir == "desc";
        IEnumerable<JsonElement> expected = sortBy switch
        {
            "title" => desc
                ? newestFirst.OrderByDescending(TitleOf, StringComparer.OrdinalIgnoreCase)
                : newestFirst.OrderBy(TitleOf, StringComparer.OrdinalIgnoreCase),
            "what" => desc
                ? newestFirst.OrderByDescending(item => item.GetProperty("message").GetString(), StringComparer.OrdinalIgnoreCase)
                : newestFirst.OrderBy(item => item.GetProperty("message").GetString(), StringComparer.OrdinalIgnoreCase),
            _ => desc ? newestFirst : newestFirst.Reverse()
        };
        Assert.Equal(expected.Select(item => item.GetProperty("id").GetString()), ids);
    }

    [Fact]
    public async Task With_no_sort_the_history_is_newest_first_as_before_and_a_sort_it_does_not_know_is_ignored()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedForSortAsync(app);

        var plain = (await WalkAsync(app, string.Empty, pageSize: 8)).Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Equal(Enumerable.Range(0, 31).Select(i => $"id{30 - i:D2}"), plain);

        var unknown = (await WalkAsync(app, "&sortBy=nonsense&sortDir=asc", pageSize: 8)).Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Equal(plain, unknown);
        var explicitDefault = (await WalkAsync(app, "&sortBy=when&sortDir=desc", pageSize: 8)).Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Equal(plain, explicitDefault);
    }

    [Fact]
    public async Task The_filter_words_are_served_for_the_screen()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/activity/filters");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Contains(root.GetProperty("types").EnumerateArray(), item => item.GetProperty("value").GetString() == "turned-down" && item.GetProperty("label").GetString() == "Turned down");
        Assert.Equal(["indexer", "client", "processor", "connection"], root.GetProperty("sources").EnumerateArray().Select(item => item.GetProperty("value").GetString()!).ToArray());
        Assert.Equal(TurnedDownGroups.All.Count, root.GetProperty("reasons").GetArrayLength());
    }

    [Fact]
    public async Task Putting_a_file_back_and_deleting_one_for_good_are_each_written_to_activity_with_the_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Path.Combine(app.DataRoot, "library");
        Directory.CreateDirectory(root);
        var keep = Path.Combine(root, "Keep (2019).mkv");
        var drop = Path.Combine(root, "Drop (2018).mkv");
        await File.WriteAllTextAsync(keep, "x");
        await File.WriteAllTextAsync(drop, "y");
        var libraries = app.Services.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>();
        var library = await libraries.CreateLibraryAsync(
            new Deluno.Libraries.Contracts.CreateLibraryRequest("Films", "movie", "collection", root, null, null, "copy", null, null, null, null, false, false, false, null, null, null),
            CancellationToken.None);
        var bin = app.Services.GetRequiredService<Deluno.Platform.IRecycleBinService>();
        var kept = await bin.StoreReplacementAsync(library, keep, keep, CancellationToken.None);
        var dropped = await bin.StoreReplacementAsync(library, drop, drop, CancellationToken.None);

        Assert.True((await app.Client.PostAsync($"/api/recycle-bin/{kept!.Id}/restore", null)).IsSuccessStatusCode);
        Assert.True((await app.Client.DeleteAsync($"/api/recycle-bin/{dropped!.Id}")).IsSuccessStatusCode);

        var restored = Assert.Single(await ListAsync(app, "&type=restored"));
        Assert.Contains("Keep (2019).mkv", restored.GetProperty("message").GetString());
        Assert.Contains("Keep (2019).mkv", restored.GetProperty("filePath").GetString());
        var deleted = Assert.Single(await ListAsync(app, "&file=Drop%20(2018)"));
        Assert.Equal("removed", deleted.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_release_Deluno_refuses_on_its_own_is_written_once_with_its_reason_and_a_persons_own_block_is_not_doubled()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var blocked = app.Services.GetRequiredService<IBlockedReleaseRepository>();

        await blocked.BlockAsync(new BlockedRelease("b1", "key-1", "Sintel.2010.CAM", "NZBgeek", "movie", "m1", "Sintel", "noVideoStream", "No video in it.", "hash1", null, "qBit", DateTimeOffset.UtcNow, BlockedReleaseStates.Refused, BlockedReleaseOrigins.Automatic), CancellationToken.None);
        // The same release failing again is one entry, and one line.
        await blocked.BlockAsync(new BlockedRelease("b2", "key-1", "Sintel.2010.CAM", "NZBgeek", "movie", "m1", "Sintel", "noVideoStream", "No video in it.", "hash1", null, "qBit", DateTimeOffset.UtcNow, BlockedReleaseStates.Refused, BlockedReleaseOrigins.Automatic), CancellationToken.None);
        // A person's own block is written by the click that made it, not here.
        await blocked.BlockAsync(new BlockedRelease("b3", "key-2", "Moon.2009.CAM", "NZBgeek", "movie", "m2", "Moon", "manual", "You blocked it.", null, null, null, DateTimeOffset.UtcNow, BlockedReleaseStates.Refused, BlockedReleaseOrigins.Manual), CancellationToken.None);

        var items = await ListAsync(app, "&type=blocklisted");

        var line = Assert.Single(items);
        Assert.Equal("Deluno blocked Sintel.2010.CAM: No video in it.", line.GetProperty("message").GetString());
        Assert.Equal("Sintel", line.GetProperty("title").GetString());
        Assert.Equal("NZBgeek", line.GetProperty("sourceName").GetString());
        Assert.Equal("m1", line.GetProperty("relatedEntityId").GetString());
    }

    [Fact]
    public void Every_known_event_kind_has_one_type_and_a_source_or_none()
    {
        foreach (var category in ActivityEventLabels.KnownCategories)
        {
            Assert.Contains(ActivityHistory.TypeOf(category), ActivityHistory.Types.All);
            Assert.Contains(ActivityHistory.OutcomeOf(category), new[] { "good", "problem" });
        }

        Assert.Equal("problem", ActivityHistory.OutcomeOf("download.import.failed"));
        Assert.Equal("good", ActivityHistory.OutcomeOf("download.vanished.recovered"));
        Assert.Equal("turned-down", ActivityHistory.TypeOf(ActivityHistory.TurnedDownCategory));
    }
}
