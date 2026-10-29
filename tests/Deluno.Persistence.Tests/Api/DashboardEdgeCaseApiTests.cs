using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>The limits the Dashboard's three read endpoints put on what a caller may ask for.</summary>
public sealed class DashboardEdgeCaseApiTests
{
    [Fact]
    public async Task Live_extras_never_look_back_more_than_48_hours_default_to_24_when_unreadable_and_round_to_the_second()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var databases = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var now = DateTimeOffset.UtcNow;
        string At(TimeSpan ago) => now.Subtract(ago).ToString("O");
        await using (var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $$"""
                INSERT INTO activity_events (id, category, message, details_json, related_entity_type, related_entity_id, created_utc)
                VALUES ('h1', 'filesystem.import.completed', 'Imported A', NULL, 'movie', 'm1', '{{At(TimeSpan.FromHours(0.5))}}'),
                       ('h30', 'filesystem.import.completed', 'Imported B', NULL, 'movie', 'm2', '{{At(TimeSpan.FromHours(30))}}'),
                       ('h40', 'filesystem.import.completed', 'Imported C', NULL, 'movie', 'm3', '{{At(TimeSpan.FromHours(40))}}'),
                       ('h60', 'filesystem.import.completed', 'Imported D', NULL, 'movie', 'm4', '{{At(TimeSpan.FromHours(60))}}');
                """;
            await command.ExecuteNonQueryAsync();
        }

        async Task<int> ImportsAsync(string sinceUtc)
        {
            var response = await app.Client.GetAsync($"/api/dashboard/live-extras?fresh=true&sinceUtc={Uri.EscapeDataString(sinceUtc)}");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            return JsonDocument.Parse(body).RootElement.GetProperty("importsToday").GetInt32();
        }

        // A week back is held to 48 hours: the 60-hour-old import is left out, the 40-hour-old one stays.
        Assert.Equal(3, await ImportsAsync(now.AddDays(-7).ToString("O")));
        // Something that is not a date reads as the last 24 hours.
        Assert.Equal(1, await ImportsAsync("not a date"));
        // The fraction of a second is dropped, so an import 0.4 s after the whole second is still in the day that began in that second.
        var whole = new DateTimeOffset(now.AddHours(-10).UtcTicks - (now.AddHours(-10).UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        await using (var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $$"""
                INSERT INTO activity_events (id, category, message, details_json, related_entity_type, related_entity_id, created_utc)
                VALUES ('edge', 'filesystem.import.completed', 'Imported E', NULL, 'movie', 'm5', '{{whole.AddMilliseconds(400):O}}');
                """;
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(2, await ImportsAsync(whole.AddMilliseconds(900).ToString("O")));
    }

    [Fact]
    public async Task The_daily_metrics_hold_the_window_to_1_to_3650_days_and_need_a_signed_in_caller()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        async Task<JsonElement> WindowAsync(string query)
        {
            var response = await app.Client.GetAsync($"/api/dashboard/metrics{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        Assert.Equal(30, (await WindowAsync("")).GetProperty("days").GetInt32());
        Assert.Equal(1, (await WindowAsync("?days=0")).GetProperty("days").GetInt32());
        Assert.Equal(1, (await WindowAsync("?days=-9")).GetProperty("days").GetInt32());
        Assert.Equal(3650, (await WindowAsync("?days=99999")).GetProperty("days").GetInt32());
        var week = await WindowAsync("?days=7");
        Assert.Equal(7, week.GetProperty("days").GetInt32());
        Assert.Equal(7, week.GetProperty("librarySize").GetArrayLength());
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), week.GetProperty("to").GetString());

        var anonymous = await app.CreateAnonymousClient().GetAsync("/api/dashboard/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task The_stored_throughput_answers_only_the_asked_hours_and_holds_them_to_1_to_48()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = app.Services.GetRequiredService<IDownloadThroughputRepository>();
        var now = DateTimeOffset.UtcNow;
        foreach (var hoursAgo in new[] { 60.0, 40.0, 0.5 })
        {
            await repository.RecordSampleAsync(new DownloadThroughputSample(now.AddHours(-hoursAgo), 5, 1), CancellationToken.None);
        }

        async Task<DownloadThroughputWindow> WindowAsync(string query)
            => (await app.Client.GetFromJsonAsync<DownloadThroughputWindow>($"/api/download-clients/throughput{query}"))!;

        var narrow = await WindowAsync("?hours=0");
        Assert.Equal(1, narrow.Hours);
        Assert.Single(narrow.Samples);
        var wide = await WindowAsync("?hours=999");
        Assert.Equal(48, wide.Hours);
        Assert.Equal(2, wide.Samples.Count);
        Assert.True(wide.Samples[0].CapturedUtc < wide.Samples[1].CapturedUtc);
        Assert.Equal(6, (await WindowAsync("")).Hours);
    }
}
