using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Media plans over a real library and real SQLite files: saving a plan copies its profile and search timing onto the
/// libraries that use it, and a rollback restores a chosen version as a new version and applies it again. What the
/// libraries hold is read back from the database file itself.
/// </summary>
public sealed class MediaPlansOnDiskTests
{
    private static string Read(ApplicationTestHost app, string sql)
    {
        var path = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>().GetDatabasePath("platform");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static async Task<string> ProfileAsync(ApplicationTestHost app, string name, string cutoff)
    {
        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name, mediaType = "movies", cutoffQuality = cutoff, allowedQualities = "WEB 1080p,WEB 2160p",
            customFormatIds = string.Empty, upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static object PlanBody(string name, string profileId, int searchHours, int retryHours, bool enabled = true)
        => new
        {
            name, mediaType = "movies", qualityProfileId = profileId, destinationRuleId = (string?)null, customFormatIds = (string?)null,
            searchIntervalOverrideHours = searchHours, retryDelayOverrideHours = retryHours, upgradeUntilCutoff = true,
            isEnabled = enabled, notes = (string?)null
        };

    private static async Task<(string PlanId, string LibraryId, string ProfileA, string ProfileB)> ALibraryOnAPlanAsync(ApplicationTestHost app)
    {
        var profileA = await ProfileAsync(app, "Profile A", "WEB 1080p");
        var profileB = await ProfileAsync(app, "Profile B", "WEB 2160p");
        var plan = await app.Client.PostAsJsonAsync("/api/policy-sets/", PlanBody("Films", profileA, 4, 12));
        Assert.True(plan.IsSuccessStatusCode, await plan.Content.ReadAsStringAsync());
        var planId = JsonDocument.Parse(await plan.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        var root = Path.Combine(app.DataRoot, "movies");
        Directory.CreateDirectory(root);
        var library = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies", mediaType = "movies", purpose = "collection", rootPath = root, downloadsPath = (string?)null, qualityProfileId = (string?)null,
            importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        var libraryId = JsonDocument.Parse(await library.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        var assigned = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/media-plan", new { policySetId = planId });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
        return (planId, libraryId, profileA, profileB);
    }

    private static (string? Profile, string Minutes, string RetryHours) LibraryNow(ApplicationTestHost app, string libraryId)
        => (Read(app, $"SELECT quality_profile_id FROM libraries WHERE id = '{libraryId}';") is { Length: > 0 } profile ? profile : null,
            Read(app, $"SELECT search_interval_minutes FROM libraries WHERE id = '{libraryId}';"),
            Read(app, $"SELECT retry_delay_hours FROM libraries WHERE id = '{libraryId}';"));

    [Fact]
    public async Task Saving_a_plan_copies_its_profile_and_search_timing_onto_the_libraries_that_use_it_and_nothing_else()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (planId, libraryId, profileA, profileB) = await ALibraryOnAPlanAsync(app);
        Assert.Equal((profileA, "240", "12"), LibraryNow(app, libraryId));

        var saved = await app.Client.PutAsJsonAsync($"/api/policy-sets/{planId}", PlanBody("Films", profileB, 6, 10, enabled: false));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal((profileB, "360", "10"), LibraryNow(app, libraryId));
        // The plan's own enabled switch is not a library setting.
        Assert.Equal("0", Read(app, $"SELECT auto_search_enabled FROM libraries WHERE id = '{libraryId}';"));
    }

    [Fact]
    public async Task A_rollback_restores_a_chosen_version_as_a_new_rollback_version_and_applies_it_to_the_libraries()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (planId, libraryId, profileA, profileB) = await ALibraryOnAPlanAsync(app);
        Assert.True((await app.Client.PutAsJsonAsync($"/api/policy-sets/{planId}", PlanBody("Films", profileB, 6, 10))).IsSuccessStatusCode);
        Assert.Equal((profileB, "360", "10"), LibraryNow(app, libraryId));

        var rollback = await app.Client.PostAsJsonAsync($"/api/policy-sets/{planId}/rollback", new { version = 1 });

        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        Assert.Equal((profileA, "240", "12"), LibraryNow(app, libraryId));
        var versions = await app.Client.GetFromJsonAsync<JsonElement>($"/api/policy-sets/{planId}/versions");
        var all = versions.EnumerateArray().ToArray();
        Assert.Equal(3, all.Length);
        Assert.Contains(all, version => version.ToString().Contains("rollback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_rollback_to_version_zero_is_400_to_an_unknown_version_404_and_to_a_version_whose_profile_is_gone_409_with_a_code()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (planId, libraryId, profileA, profileB) = await ALibraryOnAPlanAsync(app);
        Assert.True((await app.Client.PutAsJsonAsync($"/api/policy-sets/{planId}", PlanBody("Films", profileB, 6, 10))).IsSuccessStatusCode);

        var zero = await app.Client.PostAsJsonAsync($"/api/policy-sets/{planId}/rollback", new { version = 0 });
        var unknown = await app.Client.PostAsJsonAsync($"/api/policy-sets/{planId}/rollback", new { version = 99 });
        var unknownPlan = await app.Client.PostAsJsonAsync("/api/policy-sets/no-such-plan/rollback", new { version = 1 });
        Assert.True((await app.Client.DeleteAsync($"/api/quality-profiles/{profileA}")).IsSuccessStatusCode);
        var gone = await app.Client.PostAsJsonAsync($"/api/policy-sets/{planId}/rollback", new { version = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownPlan.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
        Assert.Contains("quality_profile_missing", await gone.Content.ReadAsStringAsync());
        Assert.Equal((profileB, "360", "10"), LibraryNow(app, libraryId));
    }
}
