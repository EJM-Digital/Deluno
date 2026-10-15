using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1228: a counted problem is dismissed once and is gone from every screen that counts it. A processor hand-off, the
/// import issue raised for it (Activity's "Needs a decision", the bell, Transfers) and the System row that mirrors the
/// issue are twins; dismissing any one of them closes the others.
/// </summary>
public sealed class ADismissedProblemIsGoneFromEveryScreenTests
{
    private const string Release = "Retwice.2021.1080p.WEB-DL.DDP5.1.H.264-GOLDEN";

    /// <summary>Reads the real database file on disk, so a test proves what was saved, not only what the repositories say.</summary>
    private static string OnDiskText(ApplicationTestHost app, string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, database)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static long OnDisk(ApplicationTestHost app, string database, string sql) => long.Parse(OnDiskText(app, database, sql));

    [Fact]
    public async Task Dismissing_the_hand_off_on_Transfers_closes_its_import_issue_and_its_System_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await LostHandoffAsync(app);
        var issue = await RaiseIssueAsync(app, handoff, "processor-lost");
        var other = await RaiseIssueAsync(app, await LostHandoffAsync(app, "other-hash", "Other.2020.1080p.WEB-DL-GOLDEN"), "processor-lost");
        await SystemRowAsync(app, issue.Id);
        await SystemRowAsync(app, other.Id);

        var response = await app.Client.PostAsJsonAsync($"/api/integrations/processors/handoffs/{handoff.Id}/dismiss", new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var open = (await app.Services.GetRequiredService<IMovieCatalogRepository>().GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.Select(item => item.Id).ToArray();
        Assert.DoesNotContain(issue.Id, open);
        Assert.Contains(other.Id, open);
        var health = app.Services.GetRequiredService<IHealthCheckRepository>();
        Assert.Null(await health.GetAsync(ProblemTwins.OutputIssueRowId(issue.Id), CancellationToken.None));
        Assert.NotNull(await health.GetAsync(ProblemTwins.OutputIssueRowId(other.Id), CancellationToken.None));

        // And in the database files themselves: the closed issue is dismissed and its System row gone; the other pair is still open.
        Assert.Equal("dismissed", OnDiskText(app, "movies.db", $"SELECT status FROM movie_import_recovery_cases WHERE id = '{issue.Id}'"));
        Assert.Equal("open", OnDiskText(app, "movies.db", $"SELECT status FROM movie_import_recovery_cases WHERE id = '{other.Id}'"));
        Assert.Equal(0, OnDisk(app, "platform.db", $"SELECT COUNT(*) FROM health_check_results WHERE id = '{ProblemTwins.OutputIssueRowId(issue.Id)}'"));
        Assert.Equal(1, OnDisk(app, "platform.db", $"SELECT COUNT(*) FROM health_check_results WHERE id = '{ProblemTwins.OutputIssueRowId(other.Id)}'"));
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{handoff.Id}'"));
    }

    [Fact]
    public async Task Dismissing_the_import_issue_on_Activity_dismisses_its_hand_off_and_clears_its_System_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var handoff = await LostHandoffAsync(app);
        var issue = await RaiseIssueAsync(app, handoff, "processor-lost");
        var twin = await RaiseIssueAsync(app, handoff, "processor-output-missing");
        await SystemRowAsync(app, issue.Id);
        await SystemRowAsync(app, twin.Id);

        var response = await app.Client.PostAsync($"/api/movies/import-recovery/{issue.Id}/dismiss", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var stored = await app.Services.GetRequiredService<IProcessorRepository>().GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored?.Status);
        // The other issue about the same hand-off, and both System rows, went with it.
        Assert.Empty((await app.Services.GetRequiredService<IMovieCatalogRepository>().GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        var health = app.Services.GetRequiredService<IHealthCheckRepository>();
        Assert.Null(await health.GetAsync(ProblemTwins.OutputIssueRowId(issue.Id), CancellationToken.None));
        Assert.Null(await health.GetAsync(ProblemTwins.OutputIssueRowId(twin.Id), CancellationToken.None));

        Assert.Equal(0, OnDisk(app, "movies.db", "SELECT COUNT(*) FROM movie_import_recovery_cases WHERE status = 'open'"));
        Assert.Equal(0, OnDisk(app, "platform.db", $"SELECT COUNT(*) FROM health_check_results WHERE id IN ('{ProblemTwins.OutputIssueRowId(issue.Id)}', '{ProblemTwins.OutputIssueRowId(twin.Id)}')"));
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, OnDiskText(app, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{handoff.Id}'"));
    }

    [Fact]
    public async Task An_import_issue_about_a_hand_off_Deluno_already_closed_closes_on_the_next_settle()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        var handoff = await LostHandoffAsync(app);
        // The hand-off was closed (a person, or Deluno because the title was in the library) ...
        await processors.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, handoff.FailureMessage, CancellationToken.None);
        // ... and its import issue is open all the same, as it was on the golden run.
        var issue = await RaiseIssueAsync(app, handoff, "processor-lost");
        await SystemRowAsync(app, issue.Id);
        Assert.NotEmpty((await app.Services.GetRequiredService<IMovieCatalogRepository>().GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);

        var settled = await new HandledProblemsSettler(
                app.Services.GetRequiredService<IMovieCatalogRepository>(),
                app.Services.GetRequiredService<ISeriesCatalogRepository>(),
                app.Services.GetRequiredService<IJobQueueRepository>(),
                app.Services.GetService<IDownloadDispatchesRepository>(),
                app.Services.GetRequiredService<IActivityFeedRepository>(),
                processors)
            .SettleAsync(CancellationToken.None);

        Assert.True(settled >= 1);
        Assert.Empty((await app.Services.GetRequiredService<IMovieCatalogRepository>().GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        Assert.Null(await app.Services.GetRequiredService<IHealthCheckRepository>().GetAsync(ProblemTwins.OutputIssueRowId(issue.Id), CancellationToken.None));

        Assert.Equal("dismissed", OnDiskText(app, "movies.db", $"SELECT status FROM movie_import_recovery_cases WHERE id = '{issue.Id}'"));
        Assert.Equal(0, OnDisk(app, "platform.db", $"SELECT COUNT(*) FROM health_check_results WHERE id = '{ProblemTwins.OutputIssueRowId(issue.Id)}'"));
    }

    private static async Task<ProcessorHandoffItem> LostHandoffAsync(ApplicationTestHost app, string hash = "hash-1", string release = Release)
    {
        var repository = app.Services.GetRequiredService<IProcessorRepository>();
        var created = await repository.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest("library-1", "movies", "client-1", hash, release, $"/downloads/{release}", "Weir"),
            CancellationToken.None);
        var updated = await repository.UpdateProcessorHandoffAsync(created.Id, ProcessorHandoffStatuses.Lost, null, null, "Weir never picked it up.", CancellationToken.None);
        Assert.NotNull(updated);
        return updated;
    }

    private static async Task<MovieImportRecoveryCase> RaiseIssueAsync(ApplicationTestHost app, ProcessorHandoffItem handoff, string kind)
        => await app.Services.GetRequiredService<IMovieCatalogRepository>().AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(
                handoff.ReleaseName,
                kind,
                $"Weir never picked up {handoff.ReleaseName} ({kind}).",
                "Send it again.",
                JsonSerializer.Serialize(new { libraryId = handoff.LibraryId, handoffId = handoff.Id, clientId = handoff.ClientId, queueItemId = handoff.QueueItemId })),
            CancellationToken.None);

    private static Task SystemRowAsync(ApplicationTestHost app, string caseId)
        => app.Services.GetRequiredService<IHealthCheckRepository>().UpsertResultsAsync(
            [HealthCheckOutcome.Problem(ProblemTwins.OutputIssueRowId(caseId), HealthCheckCategories.Processor, "Retwice: missing from a processor's output", "Weir named a file Deluno cannot find.", "cause", "fix")],
            CancellationToken.None);
}
