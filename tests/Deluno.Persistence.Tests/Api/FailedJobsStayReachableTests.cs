using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A failed job can be found and cleared however old it is.
///
/// <para><b>Why this file exists.</b> Before the soak, the rig's sidebar read
/// "2 failed jobs (2 gave up)" and sent the owner to Activity, which read
/// "Failed 0, none failing" with its Retry button disabled. Both were honest:
/// the sidebar read every page of <c>GET /api/jobs</c>, Activity read the latest
/// 24, and the two failures sat at positions 234 and 326. The media read alone
/// adds two rows every half hour, so any failure leaves a "latest few" window
/// within hours, and from then on no screen could show it or clear it.</para>
///
/// <para>One of the two could not be cleared by retrying either: it imported a
/// file that no longer existed, so Retry would only have dead-lettered it again.
/// A counted problem needs a way out that does not depend on it succeeding.</para>
/// </summary>
public sealed class FailedJobsStayReachableTests
{
    [Fact]
    public async Task A_failed_job_buried_under_newer_work_is_still_listed_when_asked_for_by_name()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var buried = await EnqueueAsync(app, "library.search.tv", "buried-failure");
        await SetStatusAsync(app, buried.Id, "dead-letter");
        foreach (var index in Enumerable.Range(1, 30))
        {
            var newer = await EnqueueAsync(app, "media.probe", $"newer-{index}");
            await SetStatusAsync(app, newer.Id, "completed");
        }

        var latest = await ItemsAsync(app, "/api/jobs?pageSize=24");
        var failed = await ItemsAsync(app, "/api/jobs?failed=true&pageSize=24");

        Assert.DoesNotContain(latest, item => item.GetProperty("id").GetString() == buried.Id);
        var only = Assert.Single(failed);
        Assert.Equal(buried.Id, only.GetProperty("id").GetString());
    }

    [Fact]
    public async Task The_failed_list_holds_both_kinds_of_failure_and_nothing_else()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var waitingToRetry = await EnqueueAsync(app, "library.search.tv", "will-retry");
        var gaveUp = await EnqueueAsync(app, "filesystem.import.execute", "gave-up");
        var fine = await EnqueueAsync(app, "media.probe", "fine");
        await SetStatusAsync(app, waitingToRetry.Id, "failed");
        await SetStatusAsync(app, gaveUp.Id, "dead-letter");
        await SetStatusAsync(app, fine.Id, "completed");

        var ids = (await ItemsAsync(app, "/api/jobs?failed=true")).Select(item => item.GetProperty("id").GetString()).ToArray();

        Assert.Equal(2, ids.Length);
        Assert.Contains(waitingToRetry.Id, ids);
        Assert.Contains(gaveUp.Id, ids);
    }

    [Fact]
    public async Task Dismissing_a_failed_job_takes_it_off_the_queue_and_keeps_why_it_failed_in_the_feed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var job = await EnqueueAsync(app, "filesystem.import.execute", "vanished-file");
        await SetStatusAsync(app, job.Id, "dead-letter");

        var response = await app.Client.PostAsJsonAsync($"/api/jobs/{job.Id}/dismiss", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Empty(await ItemsAsync(app, "/api/jobs?failed=true"));

        var dismissal = Assert.Single(
            await ItemsAsync(app, "/api/activity?pageSize=50"),
            item => item.GetProperty("category").GetString() == "job.dismissed");
        Assert.Contains("Import a download", dismissal.GetProperty("message").GetString());
        Assert.Contains("The file is gone.", dismissal.GetProperty("detailsJson").GetString());

        // A second press finds nothing to dismiss and says so, rather than erroring.
        var again = await app.Client.PostAsJsonAsync($"/api/jobs/{job.Id}/dismiss", new { });
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Only_a_failed_job_can_be_dismissed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var queued = await EnqueueAsync(app, "library.search.tv", "still-wanted");

        var response = await app.Client.PostAsJsonAsync($"/api/jobs/{queued.Id}/dismiss", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(await ItemsAsync(app, "/api/jobs"), item => item.GetProperty("id").GetString() == queued.Id);
    }

    private static Task<JobQueueItem> EnqueueAsync(ApplicationTestHost app, string jobType, string dedupeKey)
        => app.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest(
                JobType: jobType,
                Source: "test",
                PayloadJson: "{}",
                RelatedEntityType: null,
                RelatedEntityId: null,
                DedupeKey: dedupeKey),
            CancellationToken.None);

    private static async Task<JsonElement[]> ItemsAsync(ApplicationTestHost app, string path)
    {
        var response = await app.Client.GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    /// <summary>
    /// Puts a job in a finished state in SQL, because no worker runs these in a
    /// test and the states are legal ones the queue reaches on its own.
    /// </summary>
    private static async Task SetStatusAsync(ApplicationTestHost app, string jobId, string status)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE job_queue
            SET status = $status,
                last_error = CASE WHEN $status = 'completed' THEN NULL ELSE 'The file is gone.' END,
                completed_utc = $completedUtc
            WHERE id = $id;
            """;
        AddParameter(command, "$status", status);
        AddParameter(command, "$completedUtc", DateTimeOffset.UtcNow.ToString("O"));
        AddParameter(command, "$id", jobId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
