using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>The words a processor may use to report on a hand-off, and what Deluno records for each (#1329).</summary>
public sealed class ProcessorEventStatusesLowGapTests
{
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    private static async Task<(string LibraryId, string HandoffId, string Output)> SceneAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films", mediaType = "movies", rootPath = root, downloadsPath = downloads,
            importWorkflow = "refine-before-import", processorName = "Weir", processorOutputPath = output
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = (await response.Content.ReadFromJsonAsync<LibraryItem>())!;
        var repository = app.Services.GetRequiredService<IProcessorRepository>();
        var handoff = await repository.EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(library.Id, "movies", "sabnzbd", "nzo-1", Release, Path.Combine(downloads, Release), "Weir"),
            CancellationToken.None);
        await repository.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, null, CancellationToken.None);
        return (library.Id, handoff.Id, output);
    }

    private static async Task<(HttpStatusCode Code, JsonElement Body)> ReportAsync(ApplicationTestHost app, object report)
    {
        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", report);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Processing_means_started_error_means_failed_and_a_word_Deluno_does_not_know_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoffId, _) = await SceneAsync(app);

        var processing = await ReportAsync(app, new { libraryId, handoffId, status = "Processing" });
        var error = await ReportAsync(app, new { libraryId, handoffId, status = "error", message = "Weir could not read the file." });
        var unknown = await ReportAsync(app, new { libraryId, handoffId, status = "working-on-it" });

        Assert.Equal((HttpStatusCode.Accepted, "started"), (processing.Code, processing.Body.GetProperty("status").GetString()));
        Assert.Equal((HttpStatusCode.Accepted, "failed"), (error.Code, error.Body.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Code);
    }

    [Fact]
    public async Task Processed_and_ready_mean_completed_and_a_completed_report_without_a_cleaned_output_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoffId, root) = await SceneAsync(app);
        var output = Directory.CreateDirectory(Path.Combine(root, Release)).FullName;

        var ready = await ReportAsync(app, new { libraryId, handoffId, status = "ready", outputPath = output });
        var processed = await ReportAsync(app, new { libraryId, handoffId, status = "processed", outputPath = output });
        var noOutput = await ReportAsync(app, new { libraryId, handoffId, status = "processed" });

        Assert.True(ready.Code == HttpStatusCode.Accepted, ready.Body.ToString());
        Assert.Equal("completed", ready.Body.GetProperty("status").GetString());
        Assert.Equal("completed", processed.Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, noOutput.Code);
    }

    [Fact]
    public async Task An_accepted_report_is_recorded_and_queues_no_import()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, handoffId, _) = await SceneAsync(app);

        var accepted = await ReportAsync(app, new { libraryId, handoffId, status = "accepted" });

        Assert.Equal((HttpStatusCode.Accepted, "accepted"), (accepted.Code, accepted.Body.GetProperty("status").GetString()));
        Assert.True(accepted.Body.GetProperty("importJobId").ValueKind is JsonValueKind.Null);
        var stored = await app.Services.GetRequiredService<IProcessorRepository>().GetProcessorHandoffAsync(handoffId, CancellationToken.None);
        Assert.Equal("accepted", stored!.Status);
    }
}
