using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The block list over a real host and its real database file: every route needs sign-in and a refused call leaves the
/// database as it was, and lifting a block writes its plain Activity line, starts no search and says 404 for an unknown block.
/// </summary>
public sealed class BlockedReleasesOnDiskTests
{
    private static long Count(ApplicationTestHost app, string sql)
    {
        var path = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>().GetDatabasePath("jobs");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task Every_block_list_route_answers_401_without_a_sign_in_and_changes_nothing_in_the_database()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();
        var before = Count(app, "SELECT COUNT(*) FROM blocked_releases;");

        var responses = new[]
        {
            await anonymous.PostAsJsonAsync("/api/blocked-releases", new { releaseName = "Sintel 2010 WEB 1080p" }),
            await anonymous.GetAsync("/api/blocked-releases"),
            await anonymous.DeleteAsync("/api/blocked-releases/anything"),
            await anonymous.PostAsync("/api/blocked-releases/anything/refuse", null),
            await anonymous.PostAsync("/api/blocked-releases/anything/cleanup", null),
            await anonymous.PostAsync("/api/blocked-releases/cleanup-all", null)
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(before, Count(app, "SELECT COUNT(*) FROM blocked_releases;"));
    }

    [Fact]
    public async Task Lifting_a_block_answers_204_writes_the_plain_activity_line_starts_no_search_and_an_unknown_block_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var made = await app.Client.PostAsJsonAsync("/api/blocked-releases", new { releaseName = "Sintel 2010 WEB 1080p", indexerName = "Test", title = "Sintel" });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await made.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        Assert.Equal(1, Count(app, "SELECT COUNT(*) FROM blocked_releases;"));
        var jobsBefore = Count(app, "SELECT COUNT(*) FROM job_queue;");

        var lifted = await app.Client.DeleteAsync($"/api/blocked-releases/{id}");
        var again = await app.Client.DeleteAsync($"/api/blocked-releases/{id}");
        var unknown = await app.Client.DeleteAsync("/api/blocked-releases/no-such-block");

        Assert.Equal(HttpStatusCode.NoContent, lifted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(0, Count(app, "SELECT COUNT(*) FROM blocked_releases;"));
        Assert.Equal(jobsBefore, Count(app, "SELECT COUNT(*) FROM job_queue;"));
        var activity = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("You lifted the block on", activity);
        Assert.Contains("No search was started.", activity);
    }
}
