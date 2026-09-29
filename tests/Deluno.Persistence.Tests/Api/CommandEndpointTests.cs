using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.ApiParity;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The one address other programs send actions to (#1019), the way Radarr's
/// and Sonarr's <c>POST /api/v3/command</c> is used: name in, command out, 201.
/// </summary>
public sealed class CommandEndpointTests
{
    [Fact]
    public async Task A_backup_command_answers_created_with_a_completed_command_and_makes_the_backup()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/command", new { name = "backup" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var command = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Backup", command.GetProperty("name").GetString());
        Assert.Equal("completed", command.GetProperty("status").GetString());
        Assert.Equal("successful", command.GetProperty("result").GetString());
        Assert.Equal("manual", command.GetProperty("trigger").GetString());
        Assert.EndsWith(command.GetProperty("id").GetString()!, response.Headers.Location!.ToString());

        var backups = await app.Client.GetFromJsonAsync<JsonElement>("/api/backups");
        Assert.Equal(1, backups.GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_command_is_a_400_that_lists_the_ones_Deluno_runs()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/command", new { name = "MakeCoffee" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var said = await response.Content.ReadAsStringAsync();
        Assert.Contains("MakeCoffee", said);
        Assert.Contains("RssSync", said);
        Assert.Contains("Backup", said);
    }

    [Fact]
    public async Task A_command_without_a_name_is_a_400()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/command", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Checking_feeds_queues_one_sync_per_switched_on_source_and_the_command_follows_the_job()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/intake-sources", new
        {
            name = "Watchlist",
            provider = "rss",
            feedUrl = "https://example.invalid/feed.xml",
            isEnabled = true,
            mediaType = "movies"
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        var response = await app.Client.PostAsJsonAsync("/api/command", new { name = "RssSync" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var command = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobIds = command.GetProperty("jobIds").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var jobId = Assert.Single(jobIds);
        Assert.Equal("queued", command.GetProperty("status").GetString());

        var job = await app.Services.GetRequiredService<IJobQueueRepository>().GetAsync(jobId, CancellationToken.None);
        Assert.Equal("intake.sync", job!.JobType);

        var read = await app.Client.GetFromJsonAsync<JsonElement>($"/api/command/{command.GetProperty("id").GetString()}");
        Assert.Equal("RssSync", read.GetProperty("name").GetString());

        var activity = await app.Client.GetStringAsync("/api/activity");
        Assert.Contains("asked Deluno to run RssSync", activity);
    }

    [Fact]
    public async Task Refreshing_a_film_by_id_queues_its_metadata_refresh_and_an_unknown_id_fails_the_command()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Arrival", releaseYear = 2016, monitored = true });
        var movieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var ok = await app.Client.PostAsJsonAsync("/api/command", new { name = "RefreshMovie", movieIds = new[] { movieId } });
        var okBody = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, okBody.GetProperty("jobIds").GetArrayLength());

        var missing = await app.Client.PostAsJsonAsync("/api/command", new { name = "RefreshMovie", movieId = "nope" });
        var missingBody = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("failed", missingBody.GetProperty("status").GetString());
        Assert.Contains("nope", missingBody.GetProperty("exception").GetString());
    }

    [Fact]
    public async Task The_list_returns_the_commands_asked_for_newest_first()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await app.Client.PostAsJsonAsync("/api/command", new { name = "RssSync" });
        await app.Client.PostAsJsonAsync("/api/command", new { name = "Backup" });

        var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/command");

        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("Backup", list[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/command/not-there")).StatusCode);
    }

    [Fact]
    public async Task A_key_without_the_scope_a_command_needs_is_refused_with_403()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Read only", scopes = "read" });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var readOnly = app.CreateAnonymousClient();
        readOnly.DefaultRequestHeaders.Add("X-Api-Key", key);

        var refused = await readOnly.PostAsJsonAsync("/api/command", new { name = "Backup" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Theory]
    [InlineData("ApplicationUpdate", "CheckForUpdate")]
    [InlineData("checkforupdate", "CheckForUpdate")]
    [InlineData("MissingSeriesSearch", "MissingEpisodeSearch")]
    [InlineData("RSSSYNC", "RssSync")]
    public void Names_are_matched_without_regard_to_case_and_Radarr_and_Sonarr_names_are_understood(string asked, string canonical)
        => Assert.Equal(canonical, CommandNames.Resolve(asked)!.Value.Name);
}
