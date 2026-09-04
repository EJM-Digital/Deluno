using Deluno.Persistence.Tests.Support;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Checks that answer the question they appear to answer.
///
/// <para><b>Why this file exists.</b> The 4 September E2E walk found the same
/// defect three times, wearing three coats: a check that validates something
/// <em>adjacent</em> to the thing that matters.</para>
///
/// <list type="bullet">
/// <item>#408 — path diagnostics probed the <em>parent</em> folder for writability
/// and reported it under <c>Writable</c>, so a folder that did not exist came
/// back writable.</item>
/// <item>#410 — the category check confirmed a category's <em>name</em> and never
/// its save path, so a category that sends downloads to the wrong folder
/// reported <c>ready</c>.</item>
/// <item>#411 — SABnzbd's health test probed <c>mode=version</c>, which SABnzbd
/// answers to anybody, so a wrong API key reported <c>Healthy</c>.</item>
/// </list>
///
/// <para>None of the three was visible to a suite of 1,632 passing tests,
/// because each one is only wrong when compared against something real. What can
/// be held here is the part that does not need a real service: that the answers
/// describe the subject they name.</para>
/// </summary>
public sealed class CheckHonestyTests
{
    /// <summary>
    /// #408. The four chips are rendered side by side, so they have to be about
    /// the same path — otherwise a missing folder shows a green Writable under a
    /// red "does not exist yet".
    /// </summary>
    [Fact]
    public async Task A_folder_that_does_not_exist_is_not_writable()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var missing = Path.Combine(app.DataRoot, "no-such-folder");

        var diagnostics = await DiagnoseAsync(app, missing);

        Assert.False(diagnostics.GetProperty("exists").GetBoolean());
        Assert.False(diagnostics.GetProperty("readable").GetBoolean());
        Assert.False(diagnostics.GetProperty("writable").GetBoolean());

        // The parent's writability is still worth knowing — it is what says
        // whether Deluno could create the folder — it just is not the same
        // question, so it gets its own name.
        Assert.True(diagnostics.GetProperty("parentExists").GetBoolean());
        Assert.True(diagnostics.GetProperty("parentWritable").GetBoolean());
        Assert.Contains("could create it here", diagnostics.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_folder_that_exists_reports_on_itself()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var real = Path.Combine(app.DataRoot, "a-real-folder");
        Directory.CreateDirectory(real);

        var diagnostics = await DiagnoseAsync(app, real);

        Assert.True(diagnostics.GetProperty("exists").GetBoolean());
        Assert.True(diagnostics.GetProperty("isDirectory").GetBoolean());
        Assert.True(diagnostics.GetProperty("readable").GetBoolean());
        Assert.True(diagnostics.GetProperty("writable").GetBoolean());
        Assert.Equal("Deluno can read and write this folder.", diagnostics.GetProperty("message").GetString());
    }

    /// <summary>
    /// #407. The form said "That folder does not exist yet" and then saved it
    /// anyway, and nothing in Deluno.Libraries ever creates a directory — so the
    /// library pointed at nothing until somebody noticed at import time.
    /// </summary>
    [Fact]
    public async Task A_library_cannot_be_created_pointing_at_a_folder_that_is_not_there()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var missing = Path.Combine(app.DataRoot, "not-created-yet");

        var response = await app.Client.PostAsJsonAsync("/api/libraries/", NewLibrary("Nowhere", missing));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("does not exist", body);
        Assert.Contains("not-created-yet", body);
        Assert.Empty(await ApiPayload.ListIdsAsync(app.Client, "/api/libraries"));
    }

    [Fact]
    public async Task A_library_pointing_at_a_real_folder_still_saves()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var real = Path.Combine(app.DataRoot, "films");
        Directory.CreateDirectory(real);

        var id = await ApiPayload.CreateAsync(app.Client, "/api/libraries/", NewLibrary("Films", real));

        Assert.Contains(id, await ApiPayload.ListIdsAsync(app.Client, "/api/libraries"));
    }

    /// <summary>
    /// #407 again, on the path people reach for second: editing an existing
    /// library. The original fix would have been worth very little if a library
    /// could still be pointed at nothing a minute after it was created.
    /// </summary>
    [Fact]
    public async Task An_existing_library_cannot_be_repointed_at_a_folder_that_is_not_there()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var real = Path.Combine(app.DataRoot, "films");
        Directory.CreateDirectory(real);
        var id = await ApiPayload.CreateAsync(app.Client, "/api/libraries/", NewLibrary("Films", real));

        var response = await app.Client.PutAsJsonAsync($"/api/libraries/{id}", new
        {
            name = "Films",
            rootPath = Path.Combine(app.DataRoot, "gone"),
            downloadsPath = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not exist", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// #413. A bare 403 is survivable in a browser, where the UI knows what it
    /// asked for. It is not survivable in a script, which is what API keys are
    /// for.
    /// </summary>
    [Fact]
    public async Task A_wrong_scope_refusal_says_which_scope_was_needed()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Read only", scopes = "read" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var key = createdBody.RootElement.GetProperty("apiKey").GetString()!;

        using var readOnly = app.CreateAnonymousClient();
        readOnly.DefaultRequestHeaders.Add("X-Api-Key", key);

        var allowed = await readOnly.GetAsync("/api/tags/");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var refused = await readOnly.PostAsJsonAsync("/api/tags/", new { name = "Should fail" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var explanation = await refused.Content.ReadAsStringAsync();
        Assert.NotEmpty(explanation);
        using var problem = JsonDocument.Parse(explanation);
        Assert.Contains("write", problem.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "read",
            problem.RootElement.GetProperty("granted").EnumerateArray().Select(value => value.GetString()));
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<JsonElement> DiagnoseAsync(ApplicationTestHost app, string path)
    {
        var response = await app.Client.PostAsJsonAsync("/api/filesystem/path-diagnostics", new { path });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static object NewLibrary(string name, string rootPath) => new
    {
        name,
        mediaType = "movies",
        purpose = "collection",
        rootPath,
        downloadsPath = (string?)null,
        qualityProfileId = (string?)null,
        importWorkflow = "copy",
        processorName = (string?)null,
        processorOutputPath = (string?)null,
        processorTimeoutMinutes = (int?)null,
        processorFailureMode = (string?)null,
        autoSearchEnabled = false,
        missingSearchEnabled = false,
        upgradeSearchEnabled = false,
        searchIntervalHours = (int?)null,
        retryDelayHours = (int?)null,
        maxItemsPerRun = (int?)null
    };
}
