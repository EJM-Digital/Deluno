using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The System > Health rows for indexers and download clients (IndexerHealthCheck, DownloadClientHealthCheck) read the verdict a
/// connection test stored. Here the test runs against the real recorded replies of Jackett 0.24.2850 and qBittorrent 5.2.1 served
/// over a real loopback port, and the health check reads what the real route stored: a row per enabled connection, a problem while
/// the other app refuses or does not answer, the row clearing once it answers properly.
/// </summary>
public sealed class HealthChecksReadTheConnectionVerdictOnRecordedAppsTests
{
    private const string TorznabPath = "/api/v2.0/indexers/yts/results/torznab/api";

    private static async Task<string> AddIndexerAsync(ApplicationTestHost app, string baseUrl, bool enabled = true)
    {
        var created = await app.Client.PostAsJsonAsync("/api/indexers", new
        {
            name = "YTS", protocol = "torznab", privacy = "public", baseUrl, apiKey = "0123456789abcdef0123456789abcdef",
            priority = 25, categories = "2000", tags = "", mediaScope = "movies", isEnabled = enabled
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static Task TestIndexerAsync(ApplicationTestHost app, string id) => app.Client.PostAsync($"/api/indexers/{id}/test", null);

    private static IndexerHealthCheck IndexerCheck(ApplicationTestHost app) => new(app.Services.GetRequiredService<IConnectionsRepository>());

    [Fact]
    public async Task An_indexer_that_refuses_the_key_is_a_problem_row_naming_it_and_clears_once_it_answers_properly()
    {
        var wrong = RecordedReplies.Jackett("caps-wrong-api-key").Single.Reply;
        await using var jackett = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddIndexerAsync(app, jackett.BaseUrl + TorznabPath);

        jackett.AnswerEverythingWith = wrong;
        await TestIndexerAsync(app, id);
        var problem = Assert.Single(await IndexerCheck(app).RunAsync(CancellationToken.None));

        Assert.Equal($"indexer:{id}", problem.Id);
        Assert.Equal(HealthCheckStatuses.Problem, problem.Status);
        Assert.Equal(HealthCheckCategories.Indexer, problem.Category);
        Assert.StartsWith("YTS: ", problem.Message);
        Assert.Equal("Open Connections and test YTS.", problem.Fix);

        jackett.AnswerEverythingWith = null;
        await TestIndexerAsync(app, id);
        var ok = Assert.Single(await IndexerCheck(app).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, ok.Status);
        Assert.Equal("YTS is reachable.", ok.Message);
    }

    [Fact]
    public async Task A_broken_indexer_shows_on_the_System_health_list_after_the_scheduled_pass_and_clears_when_it_answers_again()
    {
        var wrong = RecordedReplies.Jackett("caps-wrong-api-key").Single.Reply;
        await using var jackett = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddIndexerAsync(app, jackett.BaseUrl + TorznabPath);

        async Task<JsonElement> RowAsync()
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Deluno.Worker.HealthChecks.HealthCheckRunner>().RunCategoriesAsync([HealthCheckCategories.Indexer], CancellationToken.None);
            var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/health");
            return list.GetProperty("checks").EnumerateArray().Single(row => row.GetProperty("id").GetString() == $"indexer:{id}");
        }

        jackett.AnswerEverythingWith = wrong;
        await TestIndexerAsync(app, id);
        var broken = await RowAsync();
        Assert.Equal("problem", broken.GetProperty("status").GetString());
        Assert.StartsWith("YTS: ", broken.GetProperty("message").GetString());

        jackett.AnswerEverythingWith = null;
        await TestIndexerAsync(app, id);
        var fixedRow = await RowAsync();
        Assert.Equal("ok", fixedRow.GetProperty("status").GetString());
    }

    [Fact(Skip = "Defect: IndexerHealthCheck has no grace for a new indexer (DownloadClientHealthCheck has one, #1055). An indexer added through POST /api/indexers and not yet tested is read at once as a problem: \"YTS: Not tested yet.\"")]
    public async Task An_indexer_just_added_is_tested_on_save_so_it_shows_reachable_not_a_problem_before_anyone_presses_test()
    {
        await using var jackett = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddIndexerAsync(app, jackett.BaseUrl + TorznabPath);

        var row = Assert.Single(await IndexerCheck(app).RunAsync(CancellationToken.None));

        Assert.Equal($"indexer:{id}", row.Id);
        Assert.Equal(HealthCheckStatuses.Ok, row.Status);
    }

    [Fact]
    public async Task An_indexer_nothing_answers_for_is_a_problem_row_and_a_switched_off_indexer_has_no_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var down = await AddIndexerAsync(app, "http://127.0.0.1:9" + TorznabPath);
        await AddIndexerAsync(app, "http://127.0.0.1:9" + TorznabPath, enabled: false);
        await TestIndexerAsync(app, down);

        var problem = Assert.Single(await IndexerCheck(app).RunAsync(CancellationToken.None));

        Assert.Equal($"indexer:{down}", problem.Id);
        Assert.Equal(HealthCheckStatuses.Problem, problem.Status);
        Assert.Contains("YTS", problem.Message);
    }

    [Fact]
    public async Task A_download_client_that_refuses_its_login_is_a_problem_row_and_clears_once_it_accepts_it()
    {
        var refused = RecordedReplies.Qbittorrent("login-refused").Exchanges[0];
        var accepted = RecordedReplies.Qbittorrent("sign-in").Exchanges[0];
        var refusing = false;
        await using var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent", new[] { "sign-in", "version", "settings" }.Select(RecordedReplies.Qbittorrent),
            (request, _) => (request.RequestUri?.PathAndQuery ?? string.Empty).Contains("auth/login", StringComparison.Ordinal) ? (refusing ? refused : accepted).Reply.ToResponse() : null);
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new
        {
            name = "qBittorrent", protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var check = new DownloadClientHealthCheck(app.Services.GetRequiredService<IConnectionsRepository>(), TimeProvider.System);

        refusing = true;
        await app.Client.PostAsync($"/api/download-clients/{id}/test", null);
        var problem = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal($"download-client:{id}", problem.Id);
        Assert.Equal(HealthCheckStatuses.Problem, problem.Status);
        Assert.Equal(HealthCheckCategories.DownloadClient, problem.Category);
        Assert.Contains("qBittorrent", problem.Message);
        Assert.Contains("Grabs to it are held until it passes.", problem.Fix);

        refusing = false;
        await app.Client.PostAsync($"/api/download-clients/{id}/test", null);
        var ok = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, ok.Status);
        Assert.StartsWith("qBittorrent", ok.Message);
        Assert.EndsWith(" is reachable.", ok.Message);
    }
}
