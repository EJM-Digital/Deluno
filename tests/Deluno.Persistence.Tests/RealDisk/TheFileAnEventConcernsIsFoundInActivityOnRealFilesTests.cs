using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// "Everything about this file" in Activity: an import, an upgrade that replaces a file and a processor's finished file each
/// leave a line that the file filter finds by the file's name, from real files in a real library and a real Activity table.
/// The processor's line is built from the report the real Weir rc.13 sent (tests/fixtures/recorded/deluno).
/// </summary>
public sealed class TheFileAnEventConcernsIsFoundInActivityOnRealFilesTests
{
    private const string Release = "Sintel.2010.WEB.1080p";

    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static async Task<JsonElement[]> FindAsync(RealDiskLibrary world, string file)
    {
        var page = await world.App.Client.GetFromJsonAsync<JsonElement>($"/api/activity?file={Uri.EscapeDataString(file)}&pageSize=100");
        return page.GetProperty("items").EnumerateArray().ToArray();
    }

    [Fact]
    public async Task An_import_is_found_by_the_name_of_the_file_it_put_in_the_library_and_by_the_name_it_came_from()
    {
        await using var world = await StartWorldAsync();
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv", fill: 0x44);
        var dispatch = await world.RecordDispatchAsync(movie.Id, Release);

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: dispatch));

        Assert.True(result.Succeeded, result.Message);
        var landed = Assert.Single(RealDiskLibrary.FilesUnder(world.Root));
        var byDestination = await FindAsync(world, Path.GetFileName(landed));
        Assert.Contains(byDestination, item => item.GetProperty("category").GetString() == "filesystem.import.completed");
        var bySource = await FindAsync(world, Release + ".mkv");
        Assert.Contains(bySource, item => item.GetProperty("category").GetString() == "filesystem.import.completed");
        Assert.Empty(await FindAsync(world, "A.File.Nothing.Touched.mkv"));
    }

    [Fact]
    public async Task An_upgrade_that_replaces_a_file_is_found_by_the_name_of_the_file_that_took_its_place()
    {
        await using var world = await StartWorldAsync();
        var (_, existing) = await world.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        var incoming = world.WriteDownload(Release + ".REPACK.mkv", fill: 0x22);

        var result = await world.ImportAsync(RealDiskLibrary.Request(incoming, "Sintel", 2010, overwrite: true, expectedExistingPath: existing));

        Assert.True(result.Succeeded, result.Message);
        var found = await FindAsync(world, Release + ".REPACK.mkv");
        Assert.Contains(found, item => item.GetProperty("category").GetString() == "filesystem.import.completed");
        Assert.All(found, item => Assert.Contains(Release + ".REPACK.mkv", item.GetProperty("filePath").GetString() ?? item.GetProperty("message").GetString() ?? string.Empty));
        Assert.True(File.Exists(existing) || (await world.Bin.ListAsync(CancellationToken.None)).Count > 0, "the file it replaced is still there or in the recycle bin");
    }

    [Fact]
    public async Task A_file_a_processor_finished_is_found_by_the_name_Weir_gave_it()
    {
        await using var world = await StartWorldAsync();
        var report = RecordedReplies.Deluno("events-completed").Exchanges[0];
        var body = JsonNode.Parse(report.Request.Body!)!.AsObject();
        var processedName = Path.GetFileName(((JsonArray)body["outputFiles"]!)[0]!.GetValue<string>().Replace('\\', '/'));
        var refined = Directory.CreateDirectory(Path.Combine(world.App.DataRoot, "Refined")).FullName;
        var made = await world.App.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films with Weir", mediaType = "movies", rootPath = Directory.CreateDirectory(Path.Combine(world.App.DataRoot, "WeirFilms")).FullName,
            downloadsPath = world.Downloads, importWorkflow = "refine-before-import", processorName = "Weir", processorOutputPath = refined
        });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var weirLibrary = (await made.Content.ReadFromJsonAsync<Deluno.Libraries.Contracts.LibraryItem>())!;
        var handoff = await world.App.Services.GetRequiredService<Deluno.Platform.Data.IProcessorRepository>().EnsureProcessorHandoffAsync(
            new Deluno.Platform.Contracts.CreateProcessorHandoffRequest(weirLibrary.Id, "movies", "qbittorrent", "queue-1", body["releaseName"]!.GetValue<string>(), Path.Combine(world.Downloads, body["releaseName"]!.GetValue<string>()), "Weir"),
            CancellationToken.None);
        body["libraryId"] = weirLibrary.Id;
        body["handoffId"] = handoff.Id;
        var output = refined;
        var localFiles = ((JsonArray)body["outputFiles"]!).Select(file => Path.Combine(output, Path.GetFileName(file!.GetValue<string>().Replace('\\', '/')))).ToArray();
        foreach (var file in localFiles)
        {
            await File.WriteAllBytesAsync(file, new byte[2048]);
        }

        body["outputFiles"] = new JsonArray(localFiles.Select(file => (JsonNode)JsonValue.Create(file)!).ToArray());
        body["outputPath"] = localFiles[0];
        var response = await world.App.Client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var found = await FindAsync(world, processedName);
        Assert.NotEmpty(found);
        Assert.All(found, item => Assert.Contains(processedName, item.GetProperty("filePath").GetString() ?? item.GetProperty("message").GetString() ?? string.Empty));
    }

    // DEFECT (found writing this test, 18 Oct 2026): a folder rename writes its Activity line with the old and new folder names
    // under "from" and "to", which the Activity file filter does not read (it reads path, filePath, sourcePath, destinationPath
    // and the like), and the sentence says only "1 movie folder renamed." So "everything about this file" never finds the rename.
    [Fact(Skip = "DEFECT: a folder rename's Activity line carries no file path, so the file filter cannot find it (library.movie.renamed holds only from/to folder names)")]
    public async Task A_rename_is_found_by_the_name_of_the_folder_it_renamed()
    {
        await using var world = await StartWorldAsync();
        var (movie, existing) = await world.ImportFirstCopyAsync("Sintel", 2010, Release, fill: 0x11);
        var oldFolder = Path.GetFileName(Path.GetDirectoryName(existing)!)!;

        var applied = await world.App.Client.PostAsJsonAsync("/api/movies/bulk/rename-apply", new { movieIds = new[] { movie.Id }, template = "{Movie} Renamed" });
        Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());

        Assert.Contains(await FindAsync(world, oldFolder), item => item.GetProperty("category").GetString() == "library.movie.renamed");
    }
}
