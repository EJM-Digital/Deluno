using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The "Moves that did not work" card of the Keep seeding screen, on the real host and its real platform database file: a move a
/// client refused, or a torrent left alone for want of a category, is stored in the database file, listed with the download, the client
/// and the reason in words and how many times Deluno has tried, and is gone from the file once it is cleared. Clearing something that is
/// not there is a plain 404 and changes nothing.
/// </summary>
public sealed class AMoveThatDidNotWorkIsListedAndClearedOnTheRealDatabaseTests
{
    private const string ProblemsKey = "keep-seeding.problems.v1";

    private static string StoredProblems(ApplicationTestHost app)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, "platform.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", ProblemsKey);
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    [Fact]
    public async Task A_refused_move_and_a_torrent_with_no_category_are_stored_listed_with_their_reasons_and_attempts_and_cleared_from_the_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var store = app.Services.GetRequiredService<IKeepSeedingStore>();
        var now = DateTimeOffset.UtcNow;
        await store.SaveProblemAsync(new KeepSeedingProblem(
            "c1:abc", "c1", "My qBittorrent", "abc", "Example.Movie", KeepSeedingProblemKinds.Refused,
            "Deluno could not move Example.Movie to keep in My qBittorrent: it has no such category.", "keep", now, now, 4), CancellationToken.None);
        await store.SaveProblemAsync(new KeepSeedingProblem(
            "c1:def", "c1", "My qBittorrent", "def", "Other.Movie", KeepSeedingProblemKinds.NeedsCategory,
            "Other.Movie is left alone until a category is set for it.", null, now, now, 1), CancellationToken.None);
        Assert.Contains("c1:abc", StoredProblems(app));
        Assert.Contains("c1:def", StoredProblems(app));

        var view = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/keep-seeding");

        var problems = view.GetProperty("problems").EnumerateArray().ToDictionary(item => item.GetProperty("key").GetString()!);
        Assert.Equal(2, problems.Count);
        Assert.Equal("Example.Movie", problems["c1:abc"].GetProperty("releaseName").GetString());
        Assert.Equal("My qBittorrent", problems["c1:abc"].GetProperty("clientName").GetString());
        Assert.Equal(KeepSeedingProblemKinds.Refused, problems["c1:abc"].GetProperty("kind").GetString());
        Assert.Contains("has no such category", problems["c1:abc"].GetProperty("message").GetString());
        Assert.Equal(4, problems["c1:abc"].GetProperty("attempts").GetInt32());
        Assert.Equal(KeepSeedingProblemKinds.NeedsCategory, problems["c1:def"].GetProperty("kind").GetString());

        var cleared = await app.Client.PostAsync("/api/download-health/keep-seeding/problems/c1:abc/clear", null);

        Assert.True(cleared.IsSuccessStatusCode);
        Assert.DoesNotContain("c1:abc", StoredProblems(app));
        Assert.Contains("c1:def", StoredProblems(app));
        var after = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/keep-seeding");
        Assert.Equal(["c1:def"], after.GetProperty("problems").EnumerateArray().Select(item => item.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task Clearing_a_move_that_is_not_listed_is_a_404_and_changes_nothing_in_the_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var store = app.Services.GetRequiredService<IKeepSeedingStore>();
        var now = DateTimeOffset.UtcNow;
        await store.SaveProblemAsync(new KeepSeedingProblem("c1:abc", "c1", "My qBittorrent", "abc", "Example.Movie", KeepSeedingProblemKinds.Refused, "Refused.", "keep", now, now, 1), CancellationToken.None);
        var before = StoredProblems(app);

        var missing = await app.Client.PostAsync("/api/download-health/keep-seeding/problems/nobody:home/clear", null);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(before, StoredProblems(app));
    }
}
