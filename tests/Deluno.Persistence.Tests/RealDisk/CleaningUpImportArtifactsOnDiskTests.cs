using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The library check's "clean up this staging file" repair on a real temp folder. A staging or backup file can be the only
/// copy of a film, so it goes to the recycle bin where it can be restored, or it stays where it is and the answer says so.
/// It is never deleted for good, and a file that is not a Deluno artifact is refused.
/// </summary>
public sealed class CleaningUpImportArtifactsOnDiskTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static string Artifact(RealDiskLibrary world, string name = "Nosferatu.mkv.deluno-stage-abc123.tmp")
    {
        var folder = Path.Combine(world.Root, "Nosferatu (1922)");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x21, 2048).ToArray());
        return path;
    }

    private static async Task<FilesystemReconciliationIssue> TheArtifactIssueAsync(IFilesystemReconciliationService service)
        => Assert.Single((await service.ScanAsync(CancellationToken.None)).Issues, issue => issue.Kind == "partialImportArtifact");

    [Fact]
    public async Task Cleaning_up_a_staging_file_moves_it_to_the_recycle_bin_where_restore_gives_it_back()
    {
        await using var world = await StartWorldAsync();
        var artifact = Artifact(world);
        using var scope = world.App.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFilesystemReconciliationService>();
        var issue = await TheArtifactIssueAsync(service);

        var repair = await service.RepairAsync(new FilesystemReconciliationRepairRequest(issue.Id, "cleanup-artifact"), CancellationToken.None);

        Assert.True(repair.Repaired, repair.Message);
        Assert.Contains("moved to the recycle bin", repair.Message);
        Assert.False(File.Exists(artifact));
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(2048, new FileInfo(item.RecyclePath).Length);
        Assert.True((await world.Bin.RestoreAsync(item.Id, CancellationToken.None)).Success);
        Assert.True(File.Exists(artifact));
    }

    [Fact]
    public async Task Without_a_recycle_bin_the_staging_file_is_left_where_it_is_and_the_answer_says_so()
    {
        await using var world = await StartWorldAsync();
        var artifact = Artifact(world);
        using var scope = world.App.Services.CreateScope();
        var services = scope.ServiceProvider;
        var withoutBin = new FilesystemReconciliationService(
            services.GetRequiredService<ILibrariesRepository>(),
            services.GetRequiredService<IMovieCatalogRepository>(),
            services.GetRequiredService<ISeriesCatalogRepository>(),
            services.GetRequiredService<IActivityFeedRepository>(),
            TimeProvider.System);
        var issue = await TheArtifactIssueAsync(withoutBin);

        var repair = await withoutBin.RepairAsync(new FilesystemReconciliationRepairRequest(issue.Id, "cleanup-artifact"), CancellationToken.None);

        Assert.False(repair.Repaired);
        Assert.Contains("left where it is", repair.Message);
        Assert.True(File.Exists(artifact));
        Assert.Equal(2048, new FileInfo(artifact).Length);
    }

    [Fact]
    public async Task A_file_that_is_not_a_deluno_staging_artifact_is_refused_and_left_alone()
    {
        await using var world = await StartWorldAsync();
        var artifact = Artifact(world);
        var film = Path.Combine(Path.GetDirectoryName(artifact)!, "Nosferatu.1922.1080p.mkv");
        File.WriteAllBytes(film, [1, 2, 3, 4]);
        using var scope = world.App.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFilesystemReconciliationService>();
        var issue = await TheArtifactIssueAsync(service);
        // The same request, aimed at the film itself: the issue's own token read back, pointed at another path.
        var token = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(issue.Id)))!.AsObject();
        token["Path"] = film;
        var aimedAtTheFilm = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token.ToJsonString()));
        var forged = await service.RepairAsync(new FilesystemReconciliationRepairRequest(aimedAtTheFilm, "cleanup-artifact"), CancellationToken.None);

        Assert.False(forged.Repaired);
        Assert.True(File.Exists(film));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(film));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }
}
