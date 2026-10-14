using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Giving up on an unmatched import, run on real files in a real temp folder with the real recycle bin and the
/// real catalogue, blocklist and job queue. A person's media inside a library only ever leaves through the bin; a file
/// in a download area is moved to the bin; a delete that fails does not stop the rest; and only unmatched cases are touched.
/// </summary>
public sealed class GivingUpOnUnmatchedFilesOnDiskTests
{
    private static readonly DateTimeOffset Later = DateTimeOffset.UtcNow.AddDays(10);

    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        var world = await RealDiskLibrary.OpenAsync(app);
        var settings = (await app.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        settings["unmatchedImportPolicy"] = UnmatchedImportPolicies.GiveUpAfterDays;
        settings["unmatchedImportGiveUpDays"] = 3;
        var saved = await app.Client.PutAsJsonAsync("/api/settings", settings);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        return world;
    }

    private static string Write(string path, string text = "an unmatched file")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>The service as the host builds it, but reading the clock ten days on, so every case is overdue.</summary>
    private static IUnmatchedImportGiveUpService ServiceFor(RealDiskLibrary world, IServiceScope scope, bool withLibraries = true)
    {
        var services = scope.ServiceProvider;
        return new UnmatchedImportGiveUpService(
            services.GetRequiredService<IPlatformSettingsRepository>(),
            services.GetRequiredService<IMovieCatalogRepository>(),
            services.GetRequiredService<ISeriesCatalogRepository>(),
            services.GetRequiredService<IDownloadDispatchesRepository>(),
            services.GetRequiredService<IBlockedReleaseRepository>(),
            downloadClientActions: null,
            services.GetRequiredService<IActivityFeedRepository>(),
            new FixedTimeProvider(Later),
            NullLogger<UnmatchedImportGiveUpService>.Instance,
            jobScheduler: services.GetRequiredService<IJobScheduler>(),
            librariesRepository: withLibraries ? services.GetRequiredService<ILibrariesRepository>() : null,
            recycleBinService: world.Bin);
    }

    private static async Task<string> OpenCaseAsync(RealDiskLibrary world, string failureKind, string sourcePath, string? dispatchId = null)
    {
        var details = JsonSerializer.Serialize(new { DispatchId = dispatchId, SourcePath = sourcePath });
        var created = await world.Movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest("Sintel", failureKind, "Deluno could not match this file.", "Choose the film.", details),
            CancellationToken.None);
        return created.Id;
    }

    private static async Task<bool> IsOpenAsync(RealDiskLibrary world, string caseId)
        => (await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases.Any(item => item.Id == caseId && item.Status == "open");

    [Fact]
    public async Task A_file_inside_a_library_is_never_deleted_it_goes_to_the_recycle_bin()
    {
        await using var world = await StartWorldAsync();
        var stray = Write(Path.Combine(world.Root, "Strays", "Sintel.2010.mkv"));
        var caseId = await OpenCaseAsync(world, ImportFailurePolicy.Unmatched, stray);
        using var scope = world.App.Services.CreateScope();

        var handled = await ServiceFor(world, scope).RunAsync(CancellationToken.None);

        Assert.Equal(1, handled);
        Assert.False(File.Exists(stray), "the file left the library folder");
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(stray, item.OriginalPath);
        Assert.Equal("an unmatched file", File.ReadAllText(item.RecyclePath));
        Assert.False(await IsOpenAsync(world, caseId));
        var activity = await scope.ServiceProvider.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Message.Contains("moved the file to the recycle bin"));
    }

    [Fact]
    public async Task A_file_in_a_download_area_goes_to_the_recycle_bin_and_the_release_is_blocklisted_and_a_new_search_is_queued()
    {
        await using var world = await StartWorldAsync();
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");
        var leftover = Write(Path.Combine(world.Downloads, "Sintel.2010.WEB.1080p.mkv"));
        var caseId = await OpenCaseAsync(world, ImportFailurePolicy.Unmatched, leftover, dispatch);
        using var scope = world.App.Services.CreateScope();

        var handled = await ServiceFor(world, scope).RunAsync(CancellationToken.None);

        Assert.Equal(1, handled);
        Assert.False(File.Exists(leftover), "the file left the download area");
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(leftover, item.OriginalPath);
        Assert.Contains(await scope.ServiceProvider.GetRequiredService<IBlockedReleaseRepository>().ListAsync(CancellationToken.None), blocked => blocked.ReleaseName == "Sintel 2010 WEB 1080p");
        Assert.False(await IsOpenAsync(world, caseId));
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_is_logged_and_the_give_up_carries_on_with_the_blocklist_and_the_case()
    {
        await using var world = await StartWorldAsync();
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");
        var locked = Write(Path.Combine(world.Downloads, "Sintel.2010.WEB.1080p.mkv"));
        var caseId = await OpenCaseAsync(world, ImportFailurePolicy.Unmatched, locked, dispatch);
        using var block = DeleteBlock.TryApply(locked);
        if (block is null)
        {
            return; // this account can delete anything (for example root in a container), so a delete cannot be made to fail
        }

        using var scope = world.App.Services.CreateScope();
        var handled = await ServiceFor(world, scope).RunAsync(CancellationToken.None);
        block.Dispose();

        Assert.Equal(1, handled);
        Assert.True(File.Exists(locked), "the locked file was left alone");
        Assert.Equal("an unmatched file", File.ReadAllText(locked));
        Assert.Contains(await scope.ServiceProvider.GetRequiredService<IBlockedReleaseRepository>().ListAsync(CancellationToken.None), blocked => blocked.ReleaseName == "Sintel 2010 WEB 1080p");
        Assert.False(await IsOpenAsync(world, caseId), "the case is resolved all the same");
    }

    [Theory]
    [InlineData("importFailed")]
    [InlineData("corrupt")]
    [InlineData("quality")]
    public async Task Only_unmatched_cases_are_given_up_on_whatever_their_age(string failureKind)
    {
        await using var world = await StartWorldAsync();
        var stray = Write(Path.Combine(world.Downloads, "Sintel.2010.mkv"));
        var caseId = await OpenCaseAsync(world, failureKind, stray);
        using var scope = world.App.Services.CreateScope();

        var handled = await ServiceFor(world, scope).RunAsync(CancellationToken.None);

        Assert.Equal(0, handled);
        Assert.True(File.Exists(stray));
        Assert.True(await IsOpenAsync(world, caseId));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    // #1319: with no library list to ask, no file can be known not to be a person's media, so none is touched.
    [Fact]
    public async Task A_file_inside_a_library_is_not_deleted_even_when_the_service_has_no_library_list()
    {
        await using var world = await StartWorldAsync();
        var stray = Write(Path.Combine(world.Root, "Strays", "Sintel.2010.mkv"));
        await OpenCaseAsync(world, ImportFailurePolicy.Unmatched, stray);
        using var scope = world.App.Services.CreateScope();

        await ServiceFor(world, scope, withLibraries: false).RunAsync(CancellationToken.None);

        Assert.True(File.Exists(stray), "the file is left exactly where it was");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        var activity = await scope.ServiceProvider.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Message.Contains("left the file where it is because Deluno could not tell which folder the file is in"));
    }

    [Fact]
    public async Task A_file_in_no_known_folder_is_left_alone_and_the_activity_says_why()
    {
        await using var world = await StartWorldAsync();
        var elsewhere = Write(Path.Combine(world.App.DataRoot, "elsewhere", "Sintel.2010.mkv"));
        await OpenCaseAsync(world, ImportFailurePolicy.Unmatched, elsewhere);
        using var scope = world.App.Services.CreateScope();

        await ServiceFor(world, scope).RunAsync(CancellationToken.None);

        Assert.True(File.Exists(elsewhere));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        var activity = await scope.ServiceProvider.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Message.Contains("it is not in a library or a downloads folder Deluno knows"));
    }
}
