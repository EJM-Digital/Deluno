using System.Net;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// GET /api/dashboard/live-extras (#1096): what the Live screen shows beyond the
/// summary, read from stored rows only.
/// </summary>
public sealed class DashboardLiveExtrasApiTests
{
    private static string Midnight() => Uri.EscapeDataString(DateTimeOffset.UtcNow.Date.ToString("O"));

    private static async Task<JsonElement> GetAsync(ApplicationTestHost app)
    {
        var response = await app.Client.GetAsync($"/api/dashboard/live-extras?sinceUtc={Midnight()}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task An_empty_install_answers_with_empty_lists_and_zeros()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var live = await GetAsync(app);

        Assert.Equal(0, live.GetProperty("arrivals").GetArrayLength());
        Assert.Equal(0, live.GetProperty("importsToday").GetInt32());
        Assert.Equal(0, live.GetProperty("protected").GetProperty("refusedToday").GetInt32());
        Assert.Equal(0, live.GetProperty("requests").GetArrayLength());
        Assert.Equal(0, live.GetProperty("retentionReclaimBytes").GetInt64());
    }

    [Fact]
    public async Task Today_s_imports_upgrades_refusals_requests_and_reclaimable_space_come_from_the_stored_rows()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var yesterday = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");

        await ExecuteAsync(databases, DelunoDatabaseNames.Jobs,
            $$"""
            INSERT INTO activity_events (id, category, message, details_json, related_entity_type, related_entity_id, created_utc)
            VALUES ('a1', 'filesystem.import.completed', 'Imported Sintel', '{"ReleaseNameEvidence":"Sintel.2010.1080p","NewQuality":"Bluray 1080p","PreviousQuality":"WEB 720p","Upgraded":true,"FileSizeBytes":4000}', 'movie', 'm1', '{{now}}'),
                   ('a2', 'filesystem.import.completed', 'Imported Moon', '{"ReleaseNameEvidence":"Moon.2009.720p","NewQuality":"WEB 720p","Upgraded":false,"FileSizeBytes":1000}', 'movie', 'm2', '{{now}}'),
                   ('a3', 'filesystem.import.completed', 'Imported Old', NULL, 'movie', 'm3', '{{yesterday}}');
            INSERT INTO blocked_releases (id, release_key, release_name, indexer_name, media_type, reason_code, reason, blocked_utc)
            VALUES ('b1', 'k1', 'Fake.Film.2020', 'Idx', 'movies', 'noVideoStream', 'No video in it', '{{now}}'),
                   ('b2', 'k2', 'Slow.Film.2020', 'Idx', 'movies', 'downloadStalled', 'It stalled', '{{now}}'),
                   ('b3', 'k3', 'Old.Film.2020', 'Idx', 'movies', 'noVideoStream', 'No video', '{{yesterday}}');
            """);
        await ExecuteAsync(databases, DelunoDatabaseNames.Platform,
            $$"""
            INSERT INTO retention_marks (rule_id, entity_id, level, title, size_bytes, first_matched_utc, remove_after_utc, state, updated_utc)
            VALUES ('r', 'm1', 'movie', 'Leaving A', 5000, '{{now}}', '{{now}}', 'leaving', '{{now}}'),
                   ('r', 'm2', 'movie', 'Ready B', 700, '{{now}}', '{{now}}', 'ready', '{{now}}'),
                   ('r', 'm3', 'movie', 'Kept C', 9000, '{{now}}', '{{now}}', 'kept', '{{now}}');
            INSERT INTO seerr_acquisitions (id, connection_id, connection_name, media_type, entity_id, title, seerr_media_id, status, message, created_utc, updated_utc)
            VALUES ('q1', 'c', 'Seerr', 'movie', 'm1', 'Sintel', 1, 'added', NULL, '{{now}}', '{{now}}');
            """);

        var live = await GetAsync(app);

        Assert.Equal(2, live.GetProperty("importsToday").GetInt32());
        Assert.Equal(5000, live.GetProperty("importedBytesToday").GetInt64());
        var upgrades = live.GetProperty("upgrades");
        Assert.Equal(1, upgrades.GetArrayLength());
        Assert.Equal("WEB 720p", upgrades[0].GetProperty("previousQuality").GetString());
        Assert.Equal("Bluray 1080p", upgrades[0].GetProperty("quality").GetString());
        var protectedYou = live.GetProperty("protected");
        Assert.Equal(2, protectedYou.GetProperty("refusedToday").GetInt32());
        Assert.Equal(1, protectedYou.GetProperty("unsafeToday").GetInt32());
        Assert.Equal(2, protectedYou.GetProperty("latest").GetArrayLength());
        Assert.Equal(5700, live.GetProperty("retentionReclaimBytes").GetInt64());
        Assert.Equal("added", live.GetProperty("requests")[0].GetProperty("status").GetString());
    }

    private static async Task ExecuteAsync(IDelunoDatabaseConnectionFactory databases, string database, string sql)
    {
        await using var connection = await databases.OpenConnectionAsync(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
