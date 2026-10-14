using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.BlockedReleases;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The blocklist's clean-up buttons and the failure rules behind a refusal, over HTTP, against the real databases. A refusal
/// with nothing left to clear in a client is said so and nothing is deleted; a failure rule is a stored opinion that going back
/// to Deluno's own answer removes from the table instead of writing the default down.
/// </summary>
public sealed class BlocklistAndFailureRulesOnTheRealHostTests
{
    private const string Release = "Some.Movie.2024.1080p.WEB-DL-GROUP";

    private static long BlockedRows(ApplicationTestHost app) => Scalar(app, "SELECT COUNT(*) FROM blocked_releases;");

    private static long StoredRules(ApplicationTestHost app) => Scalar(app, "SELECT COUNT(*) FROM import_failure_rules;");

    private static long Scalar(ApplicationTestHost app, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, "jobs.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task Clean_up_now_on_a_refusal_with_no_client_to_ask_says_there_was_nothing_to_clear_and_keeps_the_block()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await BlockAsync(app, hash: "ABCDEF0123456789");

        using var response = await app.Client.PostAsync($"/api/blocked-releases/{id}/cleanup", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString();
        Assert.True(outcome is "nothingToClear" or "clientUnavailable", $"unexpected outcome {outcome}");
        // A clean-up never lifts the block.
        Assert.Equal(1L, BlockedRows(app));
    }

    [Fact]
    public async Task Clean_up_now_on_an_id_that_is_not_there_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PostAsync("/api/blocked-releases/does-not-exist/cleanup", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Clean_up_all_with_no_refusal_that_has_a_client_and_hash_reports_zero_and_leaves_every_block()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await BlockAsync(app, hash: null);
        await BlockAsync(app, hash: null, name: "Another.Movie.2023.1080p.WEB-DL-GROUP");

        using var response = await app.Client.PostAsync("/api/blocked-releases/cleanup-all", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("cleared").GetInt32());
        Assert.Equal(2L, BlockedRows(app));
    }

    [Fact]
    public async Task Clean_up_all_needs_sign_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        using var response = await anonymous.PostAsync("/api/blocked-releases/cleanup-all", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Choosing_a_different_answer_stores_it_and_choosing_Delunos_own_answer_again_deletes_the_stored_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var rules = await app.Client.GetFromJsonAsync<JsonElement>("/api/failure-rules");
        var first = rules.EnumerateArray().First();
        var code = first.GetProperty("reasonCode").GetString()!;
        var shipped = first.GetProperty("defaultDecision");
        // Whatever Deluno's own answer is, pick a different one.
        var other = shipped.GetString() == "Never" ? "Immediately" : "Never";
        Assert.False(first.GetProperty("isOverridden").GetBoolean());
        Assert.Equal(0L, StoredRules(app));

        using var set = await app.Client.PutAsJsonAsync($"/api/failure-rules/{code}", new { decision = other });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal(1L, StoredRules(app));
        var changed = (await app.Client.GetFromJsonAsync<JsonElement>("/api/failure-rules")).EnumerateArray().Single(rule => rule.GetProperty("reasonCode").GetString() == code);
        Assert.NotEqual(shipped.ToString(), changed.GetProperty("decision").ToString());
        Assert.True(changed.GetProperty("isOverridden").GetBoolean());

        using var reset = await app.Client.DeleteAsync($"/api/failure-rules/{code}");
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(0L, StoredRules(app));
        var back = (await app.Client.GetFromJsonAsync<JsonElement>("/api/failure-rules")).EnumerateArray().Single(rule => rule.GetProperty("reasonCode").GetString() == code);
        Assert.Equal(shipped.ToString(), back.GetProperty("decision").ToString());
        Assert.False(back.GetProperty("isOverridden").GetBoolean());
    }

    [Fact]
    public async Task A_failure_rule_for_a_reason_the_pipeline_cannot_produce_is_404_and_stores_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        using var response = await app.Client.PutAsJsonAsync("/api/failure-rules/not-a-real-reason", new { decision = "never" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0L, StoredRules(app));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<string> BlockAsync(ApplicationTestHost app, string? hash, string name = Release)
    {
        using var response = await app.Client.PostAsJsonAsync(
            "/api/blocked-releases",
            new BlockReleaseRequest(name, "TestIndexer", hash, MediaType: "movie", EntityId: "movie-1", Title: "Some Movie"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
}
