using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Engines-area register rows about what /api/monitoring reports, on the real host and real stored rows: the dashboard's service
/// counts and the diagnostics search. Refs #1296.
/// </summary>
public sealed class EnginesMonitoringRowsTests
{
    private static async Task ExecuteAsync(ApplicationTestHost app, string database, string sql)
    {
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await databases.OpenConnectionAsync(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string Iso(DateTimeOffset when) => when.ToString("O", CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_dashboard_counts_healthy_indexers_and_clients_against_the_total_by_the_stored_health_status()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var up = await connections.CreateIndexerAsync(new CreateIndexerRequest("Up", "torznab", "public", "http://127.0.0.1:1/", "k", 10, "2000", "", "movies", true), CancellationToken.None);
        var down = await connections.CreateIndexerAsync(new CreateIndexerRequest("Down", "torznab", "public", "http://127.0.0.1:2/", "k", 10, "2000", "", "movies", true), CancellationToken.None);
        await connections.CreateIndexerAsync(new CreateIndexerRequest("Untested", "torznab", "public", "http://127.0.0.1:3/", "k", 10, "2000", "", "movies", true), CancellationToken.None);
        await connections.UpdateIndexerHealthAsync(up.Id, "healthy", "ok", null, 5, CancellationToken.None);
        await connections.UpdateIndexerHealthAsync(down.Id, "failing", "no", "network", null, CancellationToken.None);
        var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest("One", "sabnzbd", "localhost", 8080, null, "api-key", null, "movies", "tv", null, 1, true), CancellationToken.None);
        await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest("Two", "sabnzbd", "localhost", 8081, null, "api-key", null, "movies", "tv", null, 1, true), CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "ok", null, 5, CancellationToken.None);

        var services = (await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/dashboard")).GetProperty("services");

        Assert.Equal(1, services.GetProperty("indexersHealthy").GetInt32());
        Assert.Equal(3, services.GetProperty("indexersTotal").GetInt32());
        Assert.Equal(1, services.GetProperty("downloadClientsHealthy").GetInt32());
        Assert.Equal(2, services.GetProperty("downloadClientsTotal").GetInt32());
    }

    [Fact]
    public async Task The_dashboard_counts_running_queued_and_failed_jobs_in_the_database_not_within_the_newest_200_rows()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var when = Iso(DateTimeOffset.UtcNow);
        var sql = new StringBuilder("INSERT INTO job_queue (id, job_type, source, status, attempts, created_utc, scheduled_utc) VALUES ");
        var rows = new List<string>();
        for (var i = 0; i < 250; i++) rows.Add($"('f{i}', 'library.search.movies', 'test', '{(i % 2 == 0 ? "failed" : "dead-letter")}', 3, '{when}', '{when}')");
        for (var i = 0; i < 230; i++) rows.Add($"('q{i}', 'library.search.movies', 'test', 'queued', 0, '{when}', '{when}')");
        for (var i = 0; i < 3; i++) rows.Add($"('r{i}', 'library.search.movies', 'test', 'running', 1, '{when}', '{when}')");
        rows.Add($"('done', 'library.search.movies', 'test', 'completed', 1, '{when}', '{when}')");
        await ExecuteAsync(app, DelunoDatabaseNames.Jobs, sql.Append(string.Join(", ", rows)).Append(';').ToString());

        var services = (await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/dashboard")).GetProperty("services");

        Assert.Equal(250, services.GetProperty("failedJobs").GetInt32());
        Assert.Equal(230, services.GetProperty("queuedJobs").GetInt32());
        Assert.Equal(3, services.GetProperty("activeJobs").GetInt32());
    }

    private static async Task<JsonElement[]> DiagnosticsAsync(ApplicationTestHost app, string query)
    {
        var page = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/diagnostics" + query);
        return page.GetProperty("items").EnumerateArray().ToArray();
    }

    [Fact]
    public async Task The_diagnostics_search_reads_category_message_and_details_filters_by_category_and_time_and_lists_newest_first()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var activity = app.Services.GetRequiredService<IActivityFeedRepository>();
        await activity.RecordActivityAsync("search.completed", "Searched for Alpha", null, null, null, null, CancellationToken.None);
        await Task.Delay(15); // reason: activity rows are ordered by their timestamp, so they must not share a millisecond
        var cut = DateTimeOffset.UtcNow;
        await Task.Delay(15); // reason: the cut-off must fall between the rows
        await activity.RecordActivityAsync("grab.failed", "Grab refused", "{\"reason\":\"needle-in-details\"}", null, null, null, CancellationToken.None);
        await Task.Delay(15); // reason: keeps the order of the last two rows apart
        await activity.RecordActivityAsync("search.completed", "Searched for Beta", null, null, null, null, CancellationToken.None);

        var all = await DiagnosticsAsync(app, "?category=search.completed");
        Assert.Equal(["Searched for Beta", "Searched for Alpha"], all.Select(item => item.GetProperty("message").GetString()!).ToArray());

        var byDetails = await DiagnosticsAsync(app, "?query=needle-in-details");
        Assert.Equal("Grab refused", Assert.Single(byDetails).GetProperty("message").GetString());
        Assert.Single(await DiagnosticsAsync(app, "?query=grab.failed"));
        Assert.Single(await DiagnosticsAsync(app, "?query=Searched%20for%20Alpha"));

        var since = await DiagnosticsAsync(app, "?sinceUtc=" + Uri.EscapeDataString(cut.ToString("O", CultureInfo.InvariantCulture)));
        Assert.Equal(["Searched for Beta", "Grab refused"], since.Select(item => item.GetProperty("message").GetString()!).ToArray());

        var unparseable = await DiagnosticsAsync(app, "?sinceUtc=not-a-date&category=search.completed");
        Assert.Equal(2, unparseable.Length);

        var oneRow = await DiagnosticsAsync(app, "?pageSize=1");
        Assert.Single(oneRow);
        Assert.Equal("Searched for Beta", oneRow[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_severity_filter_finds_the_one_error_among_a_hundred_and_fifty_newer_rows_and_leaves_no_next_page()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var rows = new List<string> { $"('old-error', 'grab.failed', 'The one error', NULL, NULL, NULL, '{Iso(start)}')" };
        for (var i = 1; i <= 150; i++) rows.Add($"('ok{i}', 'search.completed', 'Fine {i}', NULL, NULL, NULL, '{Iso(start.AddSeconds(i))}')");
        await ExecuteAsync(app, DelunoDatabaseNames.Jobs,
            "INSERT INTO activity_events (id, category, message, details_json, related_entity_type, related_entity_id, created_utc) VALUES " + string.Join(", ", rows) + ";");

        var page = await app.Client.GetFromJsonAsync<JsonElement>("/api/monitoring/diagnostics?severity=error&pageSize=100");

        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("The one error", item.GetProperty("message").GetString());
        Assert.Equal("error", item.GetProperty("severity").GetString());
        Assert.True(page.GetProperty("nextPageToken").ValueKind is JsonValueKind.Null || string.IsNullOrEmpty(page.GetProperty("nextPageToken").GetString()));
    }
}
