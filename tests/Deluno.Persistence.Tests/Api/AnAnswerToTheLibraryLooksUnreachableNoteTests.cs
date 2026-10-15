using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// When a large share of a library's files is missing at once, Deluno says the library looks unreachable rather than marking
/// everything gone. "These files really are gone" is the person's answer (POST /api/filesystem/libraries/{id}/files-gone): a
/// library with no note is a 404; a note for a few files is answered at once with what was marked; a note for a thousand or more
/// is worked off in the background and answered 202 with the count. Real host, real files, real databases.
/// </summary>
public sealed class AnAnswerToTheLibraryLooksUnreachableNoteTests
{
    [Fact]
    public async Task A_library_with_no_note_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, _) = await LibraryWithFilesAsync(app, tracked: 30, present: 30);

        using var response = await app.Client.PostAsync($"/api/filesystem/libraries/{libraryId}/files-gone", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_note_for_a_few_files_is_answered_at_once_with_how_many_were_marked_gone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, _) = await LibraryWithFilesAsync(app, tracked: 30, present: 5);
        await app.Services.GetRequiredService<IFilesystemReconciliationService>().ScanAsync(CancellationToken.None);

        using var response = await app.Client.PostAsync($"/api/filesystem/libraries/{libraryId}/files-gone", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(25, body.GetProperty("markedGone").GetInt32());
        Assert.False(body.GetProperty("inBackground").GetBoolean());
        var films = await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None);
        Assert.Equal(5, films.Count(film => film.HasFile));
    }

    [Fact]
    public async Task A_note_for_a_thousand_files_or_more_is_worked_off_in_the_background_and_answered_202_with_the_count()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, _) = await LibraryWithFilesAsync(app, tracked: 1010, present: 5);
        await app.Services.GetRequiredService<IFilesystemReconciliationService>().ScanAsync(CancellationToken.None);

        using var response = await app.Client.PostAsync($"/api/filesystem/libraries/{libraryId}/files-gone", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("inBackground").GetBoolean());
        Assert.Contains("1005 files as gone in the background", body.GetProperty("message").GetString());
        await Wait.UntilAsync(
            async () => (await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None)).Count(film => film.HasFile) == 5,
            "the background marking of the missing files",
            TimeSpan.FromSeconds(60));
        // The background job writes its Activity line after it has marked the films, so the line is waited for on its
        // own: reading Activity the moment the films are marked raced the job under load (local gate, 15 Oct 2026).
        await Wait.UntilAsync(
            async () => (await app.Client.GetStringAsync("/api/activity")).Contains("library.files-gone", StringComparison.Ordinal),
            "the Activity line for the background marking",
            TimeSpan.FromSeconds(60));
    }

    private static async Task<(string LibraryId, string Root)> LibraryWithFilesAsync(ApplicationTestHost app, int tracked, int present)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        using var created = await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Films", mediaType = "movies", rootPath = root });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var requests = new List<ExistingMovieImportRequest>();
        for (var index = 0; index < tracked; index++)
        {
            var folder = Path.Combine(root, $"Film {index:0000} (2020)");
            var path = Path.Combine(folder, $"Film {index:0000} (2020) - 1080p.mkv");
            if (index < present)
            {
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(path, new byte[1024]);
            }

            requests.Add(new ExistingMovieImportRequest(
                $"Film {index:0000}", 2020, "covered", "Imported", "WEB 1080p", "WEB 1080p", true, path, 1024));
        }

        await app.Services.GetRequiredService<IMovieCatalogRepository>().ImportExistingBatchAsync(libraryId, requests, CancellationToken.None);
        return (libraryId, root);
    }
}
