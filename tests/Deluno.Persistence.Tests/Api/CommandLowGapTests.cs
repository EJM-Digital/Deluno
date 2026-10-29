using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>Low-severity rows (#1329) of the one address other programs send actions to (POST /api/command), on the whole application.</summary>
public sealed class CommandLowGapTests
{
    [Fact]
    public async Task Checking_feeds_with_no_list_switched_on_says_so_and_completes_at_once_with_no_jobs()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/command", new { name = "RssSync" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var command = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("There are no lists or feeds switched on to check.", command.GetProperty("message").GetString());
        Assert.Equal("completed", command.GetProperty("status").GetString());
        Assert.Equal("successful", command.GetProperty("result").GetString());
        Assert.Equal(0, command.GetProperty("jobIds").GetArrayLength());
        Assert.Equal(0L, long.Parse(await ApiScenes.TextAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'intake.sync'") ?? "-1"));
    }

    [Fact]
    public async Task The_command_resource_echoes_the_body_that_was_sent_with_normal_priority_a_manual_trigger_and_updates_sent_to_the_client()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/command", new { name = "RssSync", note = "from a script", count = 3 });

        var command = await response.Content.ReadFromJsonAsync<JsonElement>();
        var echoed = command.GetProperty("body");
        Assert.Equal("RssSync", echoed.GetProperty("name").GetString());
        Assert.Equal("from a script", echoed.GetProperty("note").GetString());
        Assert.Equal(3, echoed.GetProperty("count").GetInt32());
        Assert.Equal("normal", command.GetProperty("priority").GetString());
        Assert.Equal("manual", command.GetProperty("trigger").GetString());
        Assert.True(command.GetProperty("sendUpdatesToClient").GetBoolean());
        Assert.False(command.GetProperty("updateScheduledTask").GetBoolean());
    }

    [Theory]
    [InlineData("[\"RssSync\"]")]
    [InlineData("\"RssSync\"")]
    [InlineData("42")]
    [InlineData("{\"name\": 7}")]
    [InlineData("{\"name\": null}")]
    [InlineData("{\"name\": [\"RssSync\"]}")]
    [InlineData("{\"name\": \"RssSync\"")]
    [InlineData("")]
    public async Task A_body_that_is_not_a_JSON_object_or_whose_name_is_not_a_string_or_that_is_malformed_is_a_400_and_runs_nothing(string body)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/command", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await app.Client.GetFromJsonAsync<JsonElement>("/api/command")).GetArrayLength());
    }

    [Fact]
    public async Task Every_command_writes_an_Activity_line_that_says_an_outside_program_asked_and_what_came_of_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.Created, (await app.Client.PostAsJsonAsync("/api/command", new { name = "RssSync" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await app.Client.PostAsJsonAsync("/api/command", new { name = "RefreshMovie", movieId = "no-such-film" })).StatusCode);

        Assert.Equal(
            "An outside program asked Deluno to run RssSync. There are no lists or feeds switched on to check.",
            await ApiScenes.TextAsync(app, "jobs", "SELECT message FROM activity_events WHERE category = 'integration' AND message LIKE '%RssSync%'"));
        Assert.Equal(
            "An outside program asked Deluno to run RefreshMovie, but it could not: There is no film with id no-such-film.",
            await ApiScenes.TextAsync(app, "jobs", "SELECT message FROM activity_events WHERE category = 'integration' AND message LIKE '%RefreshMovie%'"));
    }

    [Fact]
    public async Task Only_the_last_hundred_commands_are_remembered_and_an_older_id_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var ids = new List<string>();
        for (var i = 0; i < 101; i++)
        {
            var response = await app.Client.PostAsJsonAsync("/api/command", new { name = "RssSync" });
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"/api/command/{ids[0]}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync($"/api/command/{ids[1]}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync($"/api/command/{ids[100]}")).StatusCode);
        var listed = await app.Client.GetFromJsonAsync<JsonElement>("/api/command");
        Assert.Equal(100, listed.GetArrayLength());
        Assert.Equal(ids[100], listed[0].GetProperty("id").GetString());
    }
}
