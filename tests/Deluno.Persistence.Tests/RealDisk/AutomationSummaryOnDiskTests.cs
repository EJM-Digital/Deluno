using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The summary the Home Assistant sensor reads, asked of a real host: on an empty install every number is zero and the
/// attention list is empty; after a library import fails because its folder vanished and a job fails, the numbers and
/// the attention item say so under the names the sensor reads.
/// </summary>
public sealed class AutomationSummaryOnDiskTests
{
    private static async Task<JsonElement> SummaryAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/automation/summary");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task On_an_empty_install_the_summary_has_every_name_the_sensor_reads_and_every_number_is_zero()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var summary = await SummaryAsync(app.Client);

        Assert.True(summary.TryGetProperty("generatedUtc", out _));
        var readiness = summary.GetProperty("readiness");
        Assert.True(readiness.TryGetProperty("status", out _));
        Assert.True(readiness.TryGetProperty("ready", out _));
        Assert.True(readiness.TryGetProperty("failedChecks", out _));
        Assert.Equal(0, summary.GetProperty("queue").GetProperty("failed").GetInt32());
        Assert.Equal(0, summary.GetProperty("queue").GetProperty("openDispatchAlerts").GetInt32());
        foreach (var name in new[] { "active", "queued" }) Assert.True(summary.GetProperty("queue").TryGetProperty(name, out _));
        foreach (var name in new[] { "active", "failed", "completed", "issues" }) Assert.Equal(0, summary.GetProperty("imports").GetProperty(name).GetInt32());
        Assert.DoesNotContain(summary.GetProperty("attention").EnumerateArray(), item => item.GetProperty("code").GetString() == "library-import.failed");
    }

    [Fact]
    public async Task A_failed_library_import_and_a_failed_job_show_in_the_summary_under_the_names_the_sensor_reads()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Path.Combine(app.DataRoot, "movies");
        Directory.CreateDirectory(Path.Combine(root, "Nosferatu (1922)"));
        File.WriteAllBytes(Path.Combine(root, "Nosferatu (1922)", "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv"), [1, 2, 3, 4]);
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies", mediaType = "movies", purpose = "collection", rootPath = root, downloadsPath = (string?)null, qualityProfileId = (string?)null,
            importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        var libraryId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        using (var scope = app.Services.CreateScope())
        {
            var import = scope.ServiceProvider.GetRequiredService<IExistingLibraryImportService>();
            var started = await import.StartAsync(libraryId, CancellationToken.None);
            Directory.Delete(root, recursive: true);
            await import.RunSliceAsync(started!.Run.Id, CancellationToken.None);

            var jobs = scope.ServiceProvider.GetRequiredService<IJobScheduler>();
            var job = await jobs.EnqueueAsync(new EnqueueJobRequest(JobType: "test.job", Source: "test", PayloadJson: "{}", RelatedEntityType: null, RelatedEntityId: null), CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
            await using var connection = await db.OpenConnectionAsync("jobs", CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE job_queue SET status = 'failed', last_error = 'It broke.' WHERE id = '{job.Id}';";
            await command.ExecuteNonQueryAsync();
        }

        var summary = await SummaryAsync(app.Client);

        Assert.Equal(1, summary.GetProperty("imports").GetProperty("failed").GetInt32());
        Assert.Equal(1, summary.GetProperty("queue").GetProperty("failed").GetInt32());
        var item = Assert.Single(summary.GetProperty("attention").EnumerateArray(), entry => entry.GetProperty("code").GetString() == "library-import.failed");
        Assert.Equal("error", item.GetProperty("severity").GetString());
        Assert.Contains("Movies", item.GetProperty("summary").GetString());
        Assert.True(item.TryGetProperty("details", out _));
        Assert.True(item.TryGetProperty("detectedUtc", out _));
    }
}
