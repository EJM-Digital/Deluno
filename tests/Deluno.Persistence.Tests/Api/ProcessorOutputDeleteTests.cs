using System.Net;
using System.Net.Http.Json;
using Deluno.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The only way anything in a processor output folder is ever removed by
/// Deluno itself (#921): a person choosing Delete on a safety-net row from
/// System &#8594; Health, never automatic, and never anything the request
/// path did not already say belongs directly inside that one library's
/// output folder.
/// </summary>
public sealed class ProcessorOutputDeleteTests
{
    [Fact]
    public async Task A_stray_file_directly_inside_the_output_folder_is_deleted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var file = Path.Combine(library.ProcessorOutputPath!, "stray.mkv");
        File.WriteAllText(file, "content");

        var response = await app.Client.PostAsJsonAsync(
            "/api/integrations/processors/output/delete",
            new { libraryId = library.Id, path = file });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task A_stray_folder_directly_inside_the_output_folder_is_deleted_recursively()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var folder = Path.Combine(library.ProcessorOutputPath!, "Season.Pack");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "e01.mkv"), "content");

        var response = await app.Client.PostAsJsonAsync(
            "/api/integrations/processors/output/delete",
            new { libraryId = library.Id, path = folder });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>Never anywhere but a direct child of the folder -- not the folder itself, and not a nested escape via "..".</summary>
    [Fact]
    public async Task A_path_outside_the_output_folder_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var elsewhere = Path.Combine(app.DataRoot, "elsewhere.txt");
        File.WriteAllText(elsewhere, "content");

        var response = await app.Client.PostAsJsonAsync(
            "/api/integrations/processors/output/delete",
            new { libraryId = library.Id, path = elsewhere });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(File.Exists(elsewhere));
    }

    [Fact]
    public async Task An_unknown_library_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync(
            "/api/integrations/processors/output/delete",
            new { libraryId = "nope", path = Path.Combine(app.DataRoot, "anything.mkv") });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<LibraryItem> CreateLibraryAsync(ApplicationTestHost app)
    {
        var processorOutputPath = Path.Combine(app.DataRoot, "processed");
        Directory.CreateDirectory(processorOutputPath);
        return await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(app.DataRoot, "movies"), Path.Combine(app.DataRoot, "downloads"), null,
                "refine-before-import", "Weir", processorOutputPath, null, null, true, true, true, 12, 24, 25),
            CancellationToken.None);
    }
}
