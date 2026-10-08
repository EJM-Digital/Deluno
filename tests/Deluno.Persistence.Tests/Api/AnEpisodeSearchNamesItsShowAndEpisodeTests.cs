using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A show-wide search queues one episode search per episode, and Activity showed twenty identical
/// "Search for an episode" rows. The job list now says which show and episode each one is for (#1180).
/// </summary>
public sealed class AnEpisodeSearchNamesItsShowAndEpisodeTests
{
    [Fact]
    public async Task Each_episode_search_names_its_show_and_episode_and_carries_the_show_so_screens_can_group_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await SeedShowAsync(app, "show-1", "Severance");
        var scheduler = app.Services.GetRequiredService<IJobScheduler>();
        foreach (var number in new[] { 9, 10 })
        {
            await scheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    "episode.search",
                    "series",
                    JsonSerializer.Serialize(new { episodeId = $"ep-{number}", seriesId = "show-1", libraryId = "lib", seasonNumber = 2, episodeNumber = number, title = "x" }),
                    "episode",
                    $"ep-{number}",
                    DedupeKey: $"episode.search:ep-{number}"),
                CancellationToken.None);
        }
        await scheduler.EnqueueAsync(new EnqueueJobRequest("media.probe", "system", "{}", null, null, DedupeKey: "other"), CancellationToken.None);

        var response = await app.Client.GetAsync("/api/jobs?pageSize=50");
        Assert.True(response.IsSuccessStatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();

        var episodes = items.Where(item => item.GetProperty("jobType").GetString() == "episode.search").ToArray();
        Assert.Equal(2, episodes.Length);
        Assert.Equal(new[] { "Severance S02E09", "Severance S02E10" }, episodes.Select(item => item.GetProperty("target").GetString()).Order().ToArray());
        Assert.All(episodes, item =>
        {
            Assert.Equal("show-1", item.GetProperty("showId").GetString());
            Assert.Equal("Severance", item.GetProperty("showTitle").GetString());
        });
        var other = items.Single(item => item.GetProperty("jobType").GetString() == "media.probe");
        Assert.Equal(JsonValueKind.Null, other.GetProperty("target").ValueKind);
    }

    private static async Task SeedShowAsync(ApplicationTestHost app, string id, string title)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO series_entries (id, title, monitored, created_utc, updated_utc) VALUES ($id, $title, 1, $now, $now);";
        foreach (var (name, value) in new[] { ("$id", id), ("$title", title), ("$now", DateTimeOffset.UtcNow.ToString("O")) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
