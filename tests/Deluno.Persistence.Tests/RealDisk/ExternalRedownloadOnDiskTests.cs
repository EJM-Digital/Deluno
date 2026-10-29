using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// "Download this title again" from another tool, through the real route on a real temp folder: the tracked file really
/// leaves its folder into the recycle bin and can be restored from it, the title is marked missing and one search is
/// queued; and a file that would not move leaves everything as it was and searches nothing.
/// </summary>
public sealed class ExternalRedownloadOnDiskTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static async Task<(string Id, string File)> AFilmInTheLibraryAsync(RealDiskLibrary world)
    {
        var folder = Path.Combine(world.Root, "Nosferatu (1922)");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv");
        File.WriteAllBytes(file, Enumerable.Repeat((byte)0x42, 4096).ToArray());
        var response = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title = "Nosferatu", releaseYear = 1922, monitored = true, libraryId = world.Library.Id, searchForMissingNow = false });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("id").GetString()!;
        Assert.True((await world.Movies.GetByIdAsync(id, CancellationToken.None))!.HasFile);
        return (id, file);
    }

    [Fact]
    public async Task A_tracked_films_file_really_leaves_its_folder_for_the_recycle_bin_is_marked_missing_and_one_search_is_queued_and_it_can_be_restored()
    {
        await using var world = await StartWorldAsync();
        var (id, file) = await AFilmInTheLibraryAsync(world);

        var response = await world.App.Client.PostAsJsonAsync("/api/integrations/external/redownload",
            new { path = file, reason = "Keep Japanese audio (removed earlier by Weir)", tool = "Weir" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(File.Exists(file), "the file is gone from the library folder");
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        // A film alone in its folder goes to the bin as the folder, with its file inside.
        Assert.All(File.ReadAllBytes(Path.Combine(item.RecyclePath, Path.GetFileName(file))), value => Assert.Equal(0x42, value));
        var movie = (await world.Movies.GetByIdAsync(id, CancellationToken.None))!;
        Assert.False(movie.HasFile);
        Assert.True(movie.Monitored);
        var searches = (await world.App.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None))
            .Where(job => job.Source == "external-redownload").ToArray();
        var search = Assert.Single(searches);
        Assert.Equal(id, search.RelatedEntityId);
        using var payload = JsonDocument.Parse(search.PayloadJson!);
        Assert.Equal(id, payload.RootElement.GetProperty("targetEntityId").GetString());
        Assert.True(payload.RootElement.GetProperty("checkMissing").GetBoolean());
        var activity = await world.App.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("Weir asked Deluno to download Nosferatu again: Keep Japanese audio (removed earlier by Weir)", activity);
        Assert.Contains("a search has started", body);

        var restored = await world.Bin.RestoreAsync(item.Id, CancellationToken.None);
        Assert.True(restored.Success, restored.Message);
        Assert.True(File.Exists(file), "restorable from the bin");
        Assert.All(File.ReadAllBytes(file), value => Assert.Equal(0x42, value));
    }

    [Fact]
    public async Task A_file_that_would_not_move_is_left_in_place_answers_409_and_searches_nothing()
    {
        await using var world = await StartWorldAsync();
        var (id, file) = await AFilmInTheLibraryAsync(world);
        using var block = DeleteBlock.Apply(file, posixFolder: world.Root);

        var response = await world.App.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path = file, reason = "test", tool = "Weir" });
        var body = await response.Content.ReadAsStringAsync();
        block.Dispose();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("so nothing was searched", body);
        Assert.True(File.Exists(file), "the file is where it was");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        var movie = (await world.Movies.GetByIdAsync(id, CancellationToken.None))!;
        Assert.True(movie.HasFile, "the catalogue is unchanged");
        Assert.DoesNotContain(await world.App.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None), job => job.Source == "external-redownload");
    }

    private sealed class RefusesTheRedownloadSearch(IJobScheduler inner) : IJobScheduler
    {
        public Task<JobQueueItem> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken)
            => request.Source == "external-redownload"
                ? throw new InvalidOperationException("The job queue is unavailable.")
                : inner.EnqueueAsync(request, cancellationToken);
    }

    [Fact]
    public async Task When_the_search_cannot_be_queued_the_file_is_already_in_the_bin_and_the_answer_is_202_with_no_search_id_and_says_to_search_from_the_title_page()
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddSingleton<IJobScheduler>(provider => new RefusesTheRedownloadSearch(provider.GetRequiredService<Deluno.Jobs.Data.SqliteJobStore>())));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        await using var world = await RealDiskLibrary.OpenAsync(app);
        var (id, file) = await AFilmInTheLibraryAsync(world);

        var response = await world.App.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path = file, reason = "test", tool = "Weir" });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("searchJobId").ValueKind);
        Assert.True(body.RootElement.GetProperty("fileRemoved").GetBoolean());
        Assert.Contains("search for it from its page", body.RootElement.GetProperty("message").GetString());
        Assert.False(File.Exists(file));
        Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.False((await world.Movies.GetByIdAsync(id, CancellationToken.None))!.HasFile);
    }
}
