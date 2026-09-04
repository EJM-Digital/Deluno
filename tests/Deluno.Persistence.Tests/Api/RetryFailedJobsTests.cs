using Deluno.Jobs.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Retrying a pile of failed jobs.
///
/// <para><b>Why this file exists.</b> #415. <c>POST /api/jobs/retry-failed</c>
/// returned <c>500</c> whenever more than one dead-lettered job shared a dedupe
/// key — which is close to always, because dead-lettering is what happens after
/// repeated attempts at the <em>same</em> work.</para>
///
/// <para>The button is only interesting once jobs have piled up, so the failure
/// mode was the normal case: an owner watching "12 failed jobs (11 gave up)"
/// pressed the one control offered and got "An unexpected error occurred", with
/// nothing in the log and no other way back.</para>
///
/// <para>The cause was a blanket <c>UPDATE … SET status='queued'</c> against a
/// partial unique index that deliberately excludes <c>dead-letter</c>. Several
/// rows may legally share a key while dead-lettered; promoting them together put
/// them in the index at once.</para>
/// </summary>
public sealed class RetryFailedJobsTests
{
    [Fact]
    public async Task Retrying_a_pile_that_shares_one_dedupe_key_requeues_the_newest_and_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobScheduler>();

        // The shape the lab produced: seventeen attempts at one import, all
        // dead-lettered, all carrying the same dedupe key.
        const string sharedKey = "filesystem.import.execute:download-client:series:none:shared";
        foreach (var attempt in Enumerable.Range(1, 5))
        {
            var job = await jobs.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "filesystem.import.execute",
                    Source: "download-client",
                    PayloadJson: $"{{\"attempt\":{attempt}}}",
                    RelatedEntityType: null,
                    RelatedEntityId: null,
                    DedupeKey: $"{sharedKey}:{attempt}"),
                CancellationToken.None);
            await DeadLetterAsync(app, job.Id, sharedKey);
        }

        var response = await app.Client.PostAsJsonAsync("/api/jobs/retry-failed", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var retried = body.RootElement.GetProperty("retried").GetInt32();

        // One row per dedupe key. The other four are the same work and stay
        // where they are — which is a decision, not a silent drop, so the count
        // returned is what actually moved.
        Assert.Equal(1, retried);
    }

    [Fact]
    public async Task Jobs_with_different_dedupe_keys_all_come_back()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobScheduler>();

        foreach (var index in Enumerable.Range(1, 3))
        {
            var job = await jobs.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "filesystem.import.execute",
                    Source: "download-client",
                    PayloadJson: "{}",
                    RelatedEntityType: null,
                    RelatedEntityId: null,
                    DedupeKey: $"distinct-work-{index}"),
                CancellationToken.None);
            await DeadLetterAsync(app, job.Id, $"distinct-work-{index}");
        }

        var response = await app.Client.PostAsJsonAsync("/api/jobs/retry-failed", new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, body.RootElement.GetProperty("retried").GetInt32());
    }

    [Fact]
    public async Task Retrying_when_nothing_has_failed_is_not_an_error()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/jobs/retry-failed", new { });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("retried").GetInt32());
    }

    /// <summary>
    /// Puts a job into the state the lab was in: dead-lettered, carrying a
    /// dedupe key it may legally share with others.
    ///
    /// <para>Done in SQL because it is a legal state the scheduler will not
    /// produce on demand — the unique index deliberately excludes
    /// <c>dead-letter</c>, which is the whole reason the blanket requeue could
    /// collide.</para>
    /// </summary>
    private static async Task DeadLetterAsync(ApplicationTestHost app, string jobId, string dedupeKey)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE job_queue
            SET status = 'dead-letter',
                dedupe_key = $dedupeKey,
                last_error = 'Season-pack import is blocked.',
                completed_utc = $completedUtc
            WHERE id = $id;
            """;
        AddParameter(command, "$dedupeKey", dedupeKey);
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
