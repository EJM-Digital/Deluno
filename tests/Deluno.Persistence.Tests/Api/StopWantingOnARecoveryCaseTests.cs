using Deluno.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Stop wanting" on a recovery case over HTTP, with real files and real database files: it ends the download, unmonitors only
/// what the download was for (an episode, not its whole show), resolves the case, writes the case's event, and a case that names
/// no download, does not exist, or is already closed is refused without changing anything.
/// </summary>
public sealed class StopWantingOnARecoveryCaseTests
{
    private const string Release = "Lost.Title.2010.1080p.WEB-DL-DELUNO";

    private static async Task<string> OpenCaseAsync(ApplicationTestHost app, string kind, string? detailsJson)
    {
        var response = await app.Client.PostAsJsonAsync($"/api/{kind}/import-recovery", new
        {
            title = Release,
            failureKind = "vanished-downloading",
            summary = "The download disappeared from the client.",
            recommendedAction = "Search again or stop wanting it.",
            detailsJson
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task On_a_show_it_unmonitors_the_episode_the_download_was_for_and_not_the_show_or_its_other_episodes()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var episodeId = show.EpisodeId;
        var dispatchId = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            show.LibraryId, "tv", "episode", episodeId, Release, "Fixture", "sabnzbd", "SABnzbd", "sent", null, cancellationToken: CancellationToken.None);
        var problems = app.Services.GetRequiredService<IDispatchProblemLog>();
        await problems.RecordAsync(dispatchId, "vanished-downloading", CancellationToken.None);
        var caseId = await OpenCaseAsync(app, "series", JsonSerializer.Serialize(new { DispatchId = dispatchId }));
        Assert.True((await series.GetInventoryDetailAsync(show.SeriesId, CancellationToken.None))!.Episodes.Single().Monitored);

        var response = await app.Client.PostAsync($"/api/series/import-recovery/{caseId}/stop-wanting", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("stopped wanting", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var detail = (await series.GetInventoryDetailAsync(show.SeriesId, CancellationToken.None))!;
        Assert.False(detail.Episodes.Single().Monitored, "the episode is no longer wanted");
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}")).GetProperty("monitored").GetBoolean(), "the show itself is still wanted");
        Assert.Equal("archived", (await app.Services.GetRequiredService<IDownloadDispatchesRepository>().GetDispatchIncludingArchivedAsync(dispatchId, CancellationToken.None))!.Status);
        Assert.Equal(0, await problems.AcknowledgeForDispatchAsync(dispatchId, CancellationToken.None));
        Assert.Equal("resolved", await ApiScenes.TextAsync(app, "series", $"SELECT status FROM series_import_recovery_cases WHERE id = '{caseId}';"));
        Assert.Equal("case_stopped_wanting", await ApiScenes.TextAsync(app, "series", $"SELECT event_kind FROM series_import_recovery_events WHERE case_id = '{caseId}';"));
        Assert.True(File.Exists(show.FilePath), "stopping wanting never deletes the file");
    }

    [Fact]
    public async Task On_a_film_it_resolves_the_case_writes_its_event_ends_the_download_and_a_second_press_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var dispatchId = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            film.LibraryId, "movies", "movie", film.MovieId, Release, "Fixture", "sabnzbd", "SABnzbd", "sent", null, cancellationToken: CancellationToken.None);
        var problems = app.Services.GetRequiredService<IDispatchProblemLog>();
        await problems.RecordAsync(dispatchId, "vanished-downloading", CancellationToken.None);
        var caseId = await OpenCaseAsync(app, "movies", JsonSerializer.Serialize(new { DispatchId = dispatchId }));

        var response = await app.Client.PostAsync($"/api/movies/import-recovery/{caseId}/stop-wanting", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}")).GetProperty("monitored").GetBoolean());
        Assert.Equal(0, await problems.AcknowledgeForDispatchAsync(dispatchId, CancellationToken.None));
        Assert.Equal("resolved", await ApiScenes.TextAsync(app, "movies", $"SELECT status FROM movie_import_recovery_cases WHERE id = '{caseId}';"));
        Assert.Equal("case_stopped_wanting", await ApiScenes.TextAsync(app, "movies", $"SELECT event_kind FROM movie_import_recovery_events WHERE case_id = '{caseId}';"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/movies/import-recovery/{caseId}/stop-wanting", content: null)).StatusCode);
        Assert.True(File.Exists(film.FilePath));
    }

    [Theory]
    [InlineData("movies")]
    [InlineData("series")]
    public async Task A_case_that_names_no_download_is_refused_in_plain_words_and_stays_open_and_an_unknown_case_is_not_found(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var caseId = await OpenCaseAsync(app, kind, null);

        var refused = await app.Client.PostAsync($"/api/{kind}/import-recovery/{caseId}/stop-wanting", content: null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("does not say which download it is about", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var table = kind == "movies" ? "movie_import_recovery_cases" : "series_import_recovery_cases";
        Assert.Equal("open", await ApiScenes.TextAsync(app, kind, $"SELECT status FROM {table} WHERE id = '{caseId}';"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/{kind}/import-recovery/no-such-case/stop-wanting", content: null)).StatusCode);
    }

    [Fact]
    public async Task Dismissing_a_show_case_settles_its_alert_writes_its_event_and_closes_its_hand_off_and_System_row_but_not_another_cases()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var processors = app.Services.GetRequiredService<IProcessorRepository>();
        var health = app.Services.GetRequiredService<IHealthCheckRepository>();
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var problems = app.Services.GetRequiredService<IDispatchProblemLog>();
        async Task<(string CaseId, string HandoffId, string DispatchId)> RaiseAsync(string hash, string release)
        {
            var handoff = await processors.EnsureProcessorHandoffAsync(new CreateProcessorHandoffRequest("library-1", "tv", "client-1", hash, release, $"/downloads/{release}", "Weir"), CancellationToken.None);
            await processors.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Lost, null, null, "Weir never picked it up.", CancellationToken.None);
            var dispatchId = await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
                "library-1", "tv", "series", "show-1", release, "Fixture", "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
            await problems.RecordAsync(dispatchId, "import-failed", CancellationToken.None);
            var made = await series.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(release, "processor-lost", "Weir never picked it up.", "Send it again.",
                    JsonSerializer.Serialize(new { libraryId = "library-1", handoffId = handoff.Id, DispatchId = dispatchId })),
                CancellationToken.None);
            await health.UpsertResultsAsync([HealthCheckOutcome.Problem(ProblemTwins.OutputIssueRowId(made.Id), HealthCheckCategories.Processor, release, "Weir named a file Deluno cannot find.", "cause", "fix")], CancellationToken.None);
            return (made.Id, handoff.Id, dispatchId);
        }

        var one = await RaiseAsync("hash-1", "The.Wire.S01E01.1080p.WEB-DL-GOLDEN");
        var other = await RaiseAsync("hash-2", "The.Wire.S01E02.1080p.WEB-DL-GOLDEN");

        var response = await app.Client.PostAsync($"/api/series/import-recovery/{one.CaseId}/dismiss", content: null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal("dismissed", await ApiScenes.TextAsync(app, "series", $"SELECT status FROM series_import_recovery_cases WHERE id = '{one.CaseId}';"));
        Assert.Equal("case_dismissed", await ApiScenes.TextAsync(app, "series", $"SELECT event_kind FROM series_import_recovery_events WHERE case_id = '{one.CaseId}';"));
        Assert.Equal(0, await problems.AcknowledgeForDispatchAsync(one.DispatchId, CancellationToken.None));
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, (await processors.GetProcessorHandoffAsync(one.HandoffId, CancellationToken.None))!.Status);
        Assert.Null(await health.GetAsync(ProblemTwins.OutputIssueRowId(one.CaseId), CancellationToken.None));
        Assert.Equal("open", await ApiScenes.TextAsync(app, "series", $"SELECT status FROM series_import_recovery_cases WHERE id = '{other.CaseId}';"));
        Assert.Equal(1, await problems.AcknowledgeForDispatchAsync(other.DispatchId, CancellationToken.None));
        Assert.NotNull(await health.GetAsync(ProblemTwins.OutputIssueRowId(other.CaseId), CancellationToken.None));
    }
}
