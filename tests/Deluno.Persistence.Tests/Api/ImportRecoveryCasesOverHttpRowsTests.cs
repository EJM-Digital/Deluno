using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: the import-recovery routes of films and of shows over HTTP, on the real host and its real database: a case is stored under
/// the kind the screens read it back by, with a default recommendation when none is given; the counts, the limit and the details
/// come out as asked; and a blank or unknown case is refused in the words the screen shows.
/// </summary>
public sealed class ImportRecoveryCasesOverHttpRowsTests
{
    public static TheoryData<string> Collections => new() { "movies", "series" };

    private static async Task<JsonElement> AddAsync(ApplicationTestHost app, string collection, string title, string? kind, string? action = "Do the thing.", string? details = null)
    {
        var response = await app.Client.PostAsJsonAsync($"/api/{collection}/import-recovery", new { title, failureKind = kind, summary = $"{title} did not import.", recommendedAction = action, detailsJson = details });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<JsonElement> SummaryAsync(ApplicationTestHost app, string collection, string query = "")
        => app.Client.GetFromJsonAsync<JsonElement>($"/api/{collection}/import-recovery{query}");

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task A_case_with_no_recommended_action_gets_the_default_words_and_a_given_one_is_kept_trimmed(string collection)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var blank = await AddAsync(app, collection, "Blank Action", "unmatched", action: "   ");
        var none = await AddAsync(app, collection, "No Action", "unmatched", action: null);
        var given = await AddAsync(app, collection, "Given Action", "unmatched", action: "  Rematch it by hand.  ");

        Assert.Equal("Review this import and decide whether Deluno should retry, rematch, or remove it.", blank.GetProperty("recommendedAction").GetString());
        Assert.Equal("Review this import and decide whether Deluno should retry, rematch, or remove it.", none.GetProperty("recommendedAction").GetString());
        Assert.Equal("Rematch it by hand.", given.GetProperty("recommendedAction").GetString());
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task Grab_and_detection_timeouts_are_stored_as_a_failed_download_and_an_unknown_kind_as_a_failed_import(string collection)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var kinds = new[] { ("A", "grab-timeout", "downloadFailed"), ("B", "detection-timeout", "downloadFailed"), ("C", "import-failed", "importFailed"), ("D", "quality", "quality"),
            ("E", "corrupt", "corrupt"), ("F", "nonsense", "importFailed"), ("G", "Download Failed", "downloadFailed"), ("H", null, "importFailed") };

        foreach (var (title, asked, stored) in kinds)
        {
            var made = await AddAsync(app, collection, $"Title {title}", asked);
            Assert.Equal(stored, made.GetProperty("failureKind").GetString());
        }
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task The_summary_has_the_five_count_fields_and_counts_each_kind_once(string collection)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        foreach (var (title, kind) in new[] { ("Q1", "quality"), ("Q2", "quality"), ("U1", "unmatched"), ("C1", "corrupt"), ("C2", "corrupt"), ("C3", "corrupt"),
            ("D1", "grab-timeout"), ("I1", "importFailed"), ("I2", "nonsense") })
        {
            await AddAsync(app, collection, title, kind);
        }

        var summary = await SummaryAsync(app, collection);

        Assert.Equal(9, summary.GetProperty("openCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("qualityCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("unmatchedCount").GetInt32());
        Assert.Equal(3, summary.GetProperty("corruptCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("downloadFailedCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("importFailedCount").GetInt32());
        Assert.Equal(9, summary.GetProperty("recentCases").GetArrayLength());
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task The_limit_shortens_the_list_but_not_the_counts_and_details_come_only_when_asked_for(string collection)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        for (var number = 1; number <= 4; number++) await AddAsync(app, collection, $"Case {number}", "corrupt", details: $"{{\"sourcePath\":\"C:/Downloads/{number}.mkv\"}}");

        var limited = await SummaryAsync(app, collection, "?limit=2");
        var plain = await SummaryAsync(app, collection);
        var detailed = await SummaryAsync(app, collection, "?includeDetails=true");

        Assert.Equal(4, limited.GetProperty("openCount").GetInt32());
        Assert.Equal(4, limited.GetProperty("corruptCount").GetInt32());
        Assert.Equal(2, limited.GetProperty("recentCases").GetArrayLength());
        Assert.All(plain.GetProperty("recentCases").EnumerateArray(), item => Assert.True(item.GetProperty("detailsJson").ValueKind == JsonValueKind.Null));
        Assert.All(detailed.GetProperty("recentCases").EnumerateArray(), item => Assert.Contains("C:/Downloads/", item.GetProperty("detailsJson").GetString(), StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task A_manual_case_with_a_blank_title_or_summary_is_a_validation_problem_and_nothing_is_stored(string collection)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var noTitle = await app.Client.PostAsJsonAsync($"/api/{collection}/import-recovery", new { title = "  ", failureKind = "quality", summary = "Something.", recommendedAction = "x" });
        var noSummary = await app.Client.PostAsJsonAsync($"/api/{collection}/import-recovery", new { title = "A Title", failureKind = "quality", summary = "", recommendedAction = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, noTitle.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noSummary.StatusCode);
        using var titleProblem = JsonDocument.Parse(await noTitle.Content.ReadAsStringAsync());
        Assert.Single(titleProblem.RootElement.GetProperty("errors").EnumerateObject());
        Assert.NotEmpty(titleProblem.RootElement.GetProperty("errors").GetProperty("title")[0].GetString()!);
        using var summaryProblem = JsonDocument.Parse(await noSummary.Content.ReadAsStringAsync());
        Assert.Equal("Add a short summary so Deluno can explain what went wrong.", summaryProblem.RootElement.GetProperty("errors").GetProperty("summary")[0].GetString());
        Assert.Equal(0, (await SummaryAsync(app, collection)).GetProperty("openCount").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task Dismissing_a_case_that_does_not_exist_or_is_already_closed_is_404_and_a_real_one_is_closed_once(string collection)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var made = await AddAsync(app, collection, "Dismiss Me", "quality");
        var id = made.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/{collection}/import-recovery/no-such-case/dismiss", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsync($"/api/{collection}/import-recovery/{id}/dismiss", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/{collection}/import-recovery/{id}/dismiss", content: null)).StatusCode);
        Assert.Equal(0, (await SummaryAsync(app, collection)).GetProperty("openCount").GetInt32());
    }
}
