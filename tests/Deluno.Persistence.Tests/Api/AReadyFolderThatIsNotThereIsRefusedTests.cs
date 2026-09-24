using System.Net;
using System.Net.Http.Json;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A processor's Ready folder is checked the same way a library's downloads
/// folder already is (#631).
///
/// <para><b>Why this file exists.</b> <c>PUT /api/libraries/{id}/workflow</c>
/// saved <c>processorOutputPath</c> after checking only that it was not blank.
/// A drive-relative value such as <c>E:WeirReadyMovies</c> -- no separator
/// after the drive letter -- passed that check and was written to the
/// library. Every file the processor handed back afterward was compared
/// against that malformed folder, was never found "inside" it, and every
/// import for the library failed silently from the moment the workflow was
/// saved, with nothing said at save time.</para>
/// </summary>
public sealed class AReadyFolderThatIsNotThereIsRefusedTests
{
    [Fact]
    public async Task A_drive_relative_processor_output_path_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app);

        var response = await PutWorkflowAsync(app, libraryId, "refine-before-import", "E:WeirReadyMovies");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("does not exist", body);
        Assert.Contains("WeirReadyMovies", body);
    }

    [Fact]
    public async Task A_well_formed_but_missing_processor_output_path_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app);
        var missing = Path.Combine(app.DataRoot, "not-created-yet");

        var response = await PutWorkflowAsync(app, libraryId, "refine-before-import", missing);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("does not exist", body);
        Assert.Contains("not-created-yet", body);
    }

    [Fact]
    public async Task A_real_existing_processor_output_folder_is_accepted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app);
        var ready = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Ready")).FullName;

        var response = await PutWorkflowAsync(app, libraryId, "refine-before-import", ready);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var saved = await response.Content.ReadFromJsonAsync<LibraryItem>();
        Assert.Equal(ready, saved!.ProcessorOutputPath);
    }

    /// <summary>
    /// A library with no processor in use has no Ready folder to check, so a
    /// malformed value sitting in the field (left over from switching away
    /// from a processor, say) does not block the save.
    /// </summary>
    [Fact]
    public async Task A_library_with_no_processor_is_not_checked_at_all()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app);

        var response = await PutWorkflowAsync(app, libraryId, "direct-import", "E:WeirReadyMovies");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        return await ApiPayload.CreateAsync(app.Client, "/api/libraries/", new
        {
            name = "Films",
            mediaType = "movies",
            rootPath = root,
            downloadsPath = downloads,
            importWorkflow = "direct-import",
            processorName = (string?)null,
            processorOutputPath = (string?)null
        });
    }

    private static Task<HttpResponseMessage> PutWorkflowAsync(
        ApplicationTestHost app,
        string libraryId,
        string importWorkflow,
        string? processorOutputPath)
        => app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/workflow", new
        {
            importWorkflow,
            processorName = importWorkflow == "refine-before-import" ? "Weir" : (string?)null,
            processorOutputPath,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null
        });
}
