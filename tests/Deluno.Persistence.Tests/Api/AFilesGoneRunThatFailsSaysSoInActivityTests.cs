using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Marking a big library's missing files as gone runs in the background; when that run stops on an error Activity says so in plain
/// words (library.files-gone-failed), the library is free to be asked again, and the answer to the first click was still 202 (coverage
/// register, engines). Refs #1296.
/// </summary>
public sealed class AFilesGoneRunThatFailsSaysSoInActivityTests
{
    /// <summary>The real reconciliation service in everything but the background answer, which breaks the way a dropped share or a locked database does.</summary>
    private sealed class BreaksWhenMarkingGone(IFilesystemReconciliationService real) : IFilesystemReconciliationService
    {
        public Task<FilesystemReconciliationReport> ScanAsync(CancellationToken cancellationToken) => real.ScanAsync(cancellationToken);

        public Task<TitleRescanResult?> RescanTitleAsync(string mediaType, string titleId, CancellationToken cancellationToken) => real.RescanTitleAsync(mediaType, titleId, cancellationToken);

        public Task<FilesystemReconciliationRepairResult> RepairAsync(FilesystemReconciliationRepairRequest request, CancellationToken cancellationToken) => real.RepairAsync(request, cancellationToken);

        public Task<LibraryFilesGoneResult?> ConfirmFilesGoneAsync(string libraryId, CancellationToken cancellationToken)
            => throw new IOException("The share stopped answering.");

        public Task<int?> GetUnreachableNoticeMissingCountAsync(string libraryId, CancellationToken cancellationToken) => real.GetUnreachableNoticeMissingCountAsync(libraryId, cancellationToken);
    }

    [Fact]
    public async Task A_background_run_that_stops_on_an_error_writes_one_plain_line_to_Activity_and_the_library_can_be_asked_again()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.Replace(ServiceDescriptor.Scoped<IFilesystemReconciliationService>(provider =>
                new BreaksWhenMarkingGone(ActivatorUtilities.CreateInstance<FilesystemReconciliationService>(provider)))));
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var libraryId = film.LibraryId;
        File.Delete(film.FilePath);
        await app.Services.GetRequiredService<IHealthCheckRepository>().UpsertResultsAsync(
            [HealthCheckOutcome.Problem(
                $"notice:library-unreachable:{libraryId}", HealthCheckCategories.Notice, "Films looks unreachable", "Most files could not be found.",
                cause: null, fix: null, detailsJson: JsonSerializer.Serialize(new { libraryId, missing = 1500, total = 1520, raisedUtc = DateTimeOffset.UtcNow }))],
            CancellationToken.None);

        using var first = await app.Client.PostAsync($"/api/filesystem/libraries/{libraryId}/files-gone", content: null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Contains("marking about 1500 files as gone in the background", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        await Wait.UntilAsync(
            async () => await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM activity_events WHERE category = 'library.files-gone-failed';") == 1,
            "the failed background run to say so in Activity");
        var said = await ApiScenes.TextAsync(app, "jobs", "SELECT message FROM activity_events WHERE category = 'library.files-gone-failed';");
        Assert.Equal("Marking the missing files as gone stopped because of an error, so some may not have been changed. Choose These files really are gone again to finish.", said);
        Assert.Equal(libraryId, await ApiScenes.TextAsync(app, "jobs", "SELECT related_entity_id FROM activity_events WHERE category = 'library.files-gone-failed';"));

        // The run is over, so the person's next click starts a new one instead of being told one is still going.
        using var again = await app.Client.PostAsync($"/api/filesystem/libraries/{libraryId}/files-gone", content: null);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.DoesNotContain("already marking", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }
}
