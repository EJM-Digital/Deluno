using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Search planning is gated on the same availability answer that pauses a library (coverage register, engines): the real worker, with
/// automatic jobs on, plans the library whose folder is there and plans nothing for the one whose folder has gone, so no release is
/// searched for and grabbed that could never be imported. Refs #1296.
/// </summary>
public sealed class AnUnreachableLibraryIsNotPlannedForSearchTests
{
    private static async Task<(string Id, string Root)> SearchingLibraryAsync(ApplicationTestHost app, string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, name)).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name,
            mediaType = "movies",
            purpose = "collection",
            rootPath = root,
            importWorkflow = "copy",
            autoSearchEnabled = true,
            missingSearchEnabled = true,
            upgradeSearchEnabled = false
        });
        response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!, root);
    }

    [Fact]
    public async Task The_worker_plans_the_library_that_is_there_and_nothing_for_the_one_whose_folder_has_gone()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddHostedService<DelunoHeartbeatWorker>());
        var present = await SearchingLibraryAsync(app, "Present");
        var gone = await SearchingLibraryAsync(app, "Gone");
        // The drive of one library goes away.
        Directory.Delete(gone.Root, recursive: true);
        // Only now does the worker start planning: its first look at the libraries finds one folder there and one gone.
        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlatformSettingsRepository>().SetGlobalAutomationEnabledAsync(true, CancellationToken.None);
        }

        await Wait.UntilAsync(
            async () => await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM library_automation_state WHERE library_id = '{present.Id}' AND last_planned_utc IS NOT NULL") == 1,
            "the worker to plan the library whose folder is there",
            TimeSpan.FromSeconds(120)); // reason: a background lane on a busy runner takes many times its usual few seconds; 120 s only stops a hang

        // The same pass planned both libraries' turn, so the gone one's absence is the gate and not a pass that has not come yet.
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM library_automation_state WHERE library_id = '{gone.Id}' AND last_planned_utc IS NOT NULL"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM job_queue WHERE related_entity_id = '{gone.Id}'"));
    }
}
