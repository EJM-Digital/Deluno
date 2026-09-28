using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #962: a processor connection's optional path pairs are applied to a
/// completion event's outputPath and outputFiles -- the processor's own
/// filesystem namespace -- translated back to Deluno's before anything below
/// compares or imports them, the same problem download client remote path
/// mappings solve (#809).
/// </summary>
public sealed class AProcessorConnectionTranslatesMappedPathsTests
{
    private const string Ours = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    [Fact]
    public async Task A_completion_reported_in_the_processors_own_path_is_imported_from_delunos_mapped_path()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        await MappingAsync(app, scene, delunoPath: scene.Output, processorPath: "/data/refined");
        var handoff = await HandoffAsync(app, scene);

        // The file really exists at Deluno's own path; the processor reports
        // it under its own view of the same folder.
        var folder = Directory.CreateDirectory(Path.Combine(scene.Output, Ours)).FullName;
        var file = Path.Combine(folder, $"{Ours}.mkv");
        await File.WriteAllBytesAsync(file, new byte[1024]);
        var processorOutputPath = $"/data/refined/{Ours}";
        var processorOutputFile = $"/data/refined/{Ours}/{Ours}.mkv";

        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "completed",
            outputPath = processorOutputPath,
            outputFiles = new[] { processorOutputFile }
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Completed, stored?.Status);
        Assert.Equal(folder, stored?.OutputPath);
        Assert.NotNull(stored?.ImportJobId);
    }

    /// <summary>A reported path that matches none of the connection's pairs passes through unchanged.</summary>
    [Fact]
    public async Task An_output_path_that_matches_no_pair_is_used_exactly_as_reported()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        await MappingAsync(app, scene, delunoPath: scene.Output, processorPath: "/data/refined");
        var handoff = await HandoffAsync(app, scene);
        var folder = Directory.CreateDirectory(Path.Combine(scene.Output, Ours)).FullName;
        var file = Path.Combine(folder, $"{Ours}.mkv");
        await File.WriteAllBytesAsync(file, new byte[1024]);

        // Reported directly in Deluno's own path, which does not start with
        // the configured processor-side root ("/data/refined"), so no pair
        // matches and Deluno uses it exactly as given.
        var response = await app.Client.PostAsJsonAsync("/api/integrations/processors/events", new
        {
            libraryId = scene.LibraryId,
            handoffId = handoff.Id,
            status = "completed",
            outputPath = folder,
            outputFiles = new[] { file }
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var stored = await Repository(app).GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(folder, stored?.OutputPath);
    }

    private sealed record Scene(string LibraryId, string Downloads, string Output);

    private static IProcessorRepository Repository(ApplicationTestHost app)
        => app.Services.GetRequiredService<IProcessorRepository>();

    private static async Task<Scene> SceneAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Downloads")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Refined")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Films",
            mediaType = "movies",
            rootPath = root,
            downloadsPath = downloads,
            importWorkflow = "refine-before-import",
            processorName = "Weir",
            processorOutputPath = output
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var library = await response.Content.ReadFromJsonAsync<LibraryItem>();

        // The library names a processor by its display name; the connection
        // row it resolves to is what path mappings are actually attached to.
        var connectionResponse = await app.Client.PostAsJsonAsync("/api/integrations/processors/connections", new
        {
            name = "Weir",
            provider = "generic-webhook",
            submissionUrl = "https://weir.example.test/webhook/deluno",
            authHeaderName = "Authorization",
            isEnabled = true
        });
        Assert.True(connectionResponse.IsSuccessStatusCode, await connectionResponse.Content.ReadAsStringAsync());

        return new Scene(library!.Id, downloads, output);
    }

    private static async Task MappingAsync(ApplicationTestHost app, Scene scene, string delunoPath, string processorPath)
    {
        var connections = await Repository(app).ListProcessorConnectionsAsync(CancellationToken.None);
        var connection = connections.Single(item => item.Name == "Weir");
        var response = await app.Client.PostAsJsonAsync(
            $"/api/integrations/processors/connections/{connection.Id}/path-mappings",
            new { delunoPath, processorPath, isEnabled = true, priority = 10 });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<ProcessorHandoffItem> HandoffAsync(ApplicationTestHost app, Scene scene)
        => await Repository(app).EnsureProcessorHandoffAsync(
            new CreateProcessorHandoffRequest(
                scene.LibraryId,
                "movies",
                "sabnzbd",
                $"nzo-{Ours}",
                Ours,
                Path.Combine(scene.Downloads, Ours),
                "Weir"),
            CancellationToken.None);
}
