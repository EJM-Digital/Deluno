using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Settings > Migration assistant over HTTP on the whole application with its own databases: a preview changes nothing,
/// Apply writes only the operations the person ticked, a second apply writes nothing twice, an invalid report is refused
/// with the report itself, applied imports are listed and reopenable, and none of it is open to someone not signed in.
/// </summary>
public sealed class MigrationAssistantRoutesTests
{
    private const string Payload = """
        {
          "qualityProfiles": [ { "id": 80, "name": "Migrated UHD", "cutoff": 3, "items": [
            { "allowed": true, "quality": { "id": 1, "name": "WEB 1080p" } },
            { "allowed": true, "quality": { "id": 3, "name": "Remux 2160p" } } ] } ],
          "rootFolders": [ { "path": "/mnt/media/migrated-movies" } ],
          "indexers": [ { "name": "Migrated Torrent", "protocol": "torrent", "baseUrl": "https://indexer.example/api", "apiKey": "secret", "categories": [2000, 2010], "enable": true } ],
          "downloadClients": [ { "name": "Migrated qBittorrent", "implementation": "QBittorrent", "host": "qbittorrent", "port": 8080,
            "fields": [ { "name": "category", "value": "movies" }, { "name": "username", "value": "deluno" }, { "name": "password", "value": "secret" } ], "enable": true } ]
        }
        """;

    /// <summary>Reads the platform database file itself, so a test shows what really reached the disk.</summary>
    private static async Task<string?> StoredAsync(ApplicationTestHost app, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<JsonElement> PreviewAsync(ApplicationTestHost app)
    {
        var response = await app.Client.PostAsJsonAsync("/api/migration/preview", new { sourceKind = "radarr", sourceName = "Radarr test", payloadJson = Payload });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_preview_lists_what_would_be_created_and_changes_nothing_in_the_databases()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profilesBefore = await StoredAsync(app, "SELECT COUNT(*) FROM quality_profiles");

        var report = await PreviewAsync(app);

        Assert.True(report.GetProperty("valid").GetBoolean());
        Assert.True(report.GetProperty("summary").GetProperty("createCount").GetInt32() >= 4);
        Assert.Equal(profilesBefore, await StoredAsync(app, "SELECT COUNT(*) FROM quality_profiles"));
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM indexer_sources"));
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM download_clients"));
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM migration_audit_reports"));
    }

    [Fact]
    public async Task Apply_writes_only_the_operations_that_were_ticked_and_records_a_report_that_can_be_listed_and_reopened()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var preview = await PreviewAsync(app);
        var indexerOperation = preview.GetProperty("operations").EnumerateArray().Single(operation => operation.GetProperty("name").GetString() == "Migrated Torrent");
        var indexerId = indexerOperation.GetProperty("id").GetString()!;

        var applied = await app.Client.PostAsJsonAsync("/api/migration/apply", new
        {
            sourceKind = "radarr", sourceName = "Radarr test", payloadJson = Payload, selectedOperationIds = new[] { indexerId }
        });

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal("1", await StoredAsync(app, "SELECT COUNT(*) FROM indexer_sources WHERE name = 'Migrated Torrent'"));
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM download_clients"));
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM quality_profiles WHERE name = 'Migrated UHD'"));
        var reports = await app.Client.GetFromJsonAsync<JsonElement>("/api/migration/reports");
        var record = Assert.Single(reports.EnumerateArray());
        Assert.Equal("Radarr test", record.GetProperty("sourceName").GetString());
        Assert.DoesNotContain("secret", record.ToString());
        var reopened = await app.Client.GetAsync($"/api/migration/reports/{record.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/migration/reports/no-such-record")).StatusCode);
    }

    [Fact]
    public async Task An_apply_of_a_payload_that_is_not_valid_is_refused_with_the_report_and_writes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var invalid = await app.Client.PostAsJsonAsync("/api/migration/apply", new { sourceKind = "radarr", sourceName = "Broken", payloadJson = "{ not json" });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var report = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(report.GetProperty("valid").GetBoolean());
        Assert.NotEqual(0, report.GetProperty("errors").GetArrayLength());
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM indexer_sources"));
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM migration_audit_reports"));
    }

    [Fact]
    public async Task Nobody_who_is_not_signed_in_can_preview_apply_or_read_the_applied_history()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();
        var body = new { sourceKind = "radarr", sourceName = "x", payloadJson = Payload };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/migration/preview", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/migration/apply", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/migration/reports")).StatusCode);
        Assert.Equal("0", await StoredAsync(app, "SELECT COUNT(*) FROM indexer_sources"));
    }
}
