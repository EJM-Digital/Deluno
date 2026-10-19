using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Prevent automatic re-add" in the shelf's bulk Remove dialog, for TV shows (coverage register, area library): removing a show that
/// an import list added, with the box ticked, writes an exclusion for that list so it does not add the show back; with the box clear
/// it writes none. The show's files stay where they are, because a bulk remove from the shelf never offers to delete them.
/// </summary>
public sealed class ARemovedShowCanBeKeptOutOfItsImportListTests
{
    [Fact]
    public async Task Removing_a_list_added_show_with_prevent_automatic_re_add_ticked_excludes_it_from_that_list_and_leaves_its_files()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "Severance", 2022);
        var source = await AddListAsync(app, show.LibraryId);
        await RecordOriginAsync(app, source, show.SeriesId, "Severance", 2022);

        var removed = await RemoveAsync(app, show.SeriesId, addImportListExclusion: true);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var result = (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];
        Assert.Equal("1", result.GetProperty("metadata").GetProperty("importListExclusionsAdded").GetString());
        var excluded = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/exclusions?mediaType=tv")).EnumerateArray().ToArray());
        Assert.Equal("Severance", excluded.GetProperty("title").GetString());
        Assert.Equal(2022, excluded.GetProperty("year").GetInt32());
        Assert.Equal(source.Id, excluded.GetProperty("sourceId").GetString());
        Assert.True(File.Exists(show.FilePath), "removing the show from Deluno did not touch its files");
        using var gone = await app.Client.GetAsync($"/api/series/{show.SeriesId}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Removing_a_list_added_show_with_the_box_clear_adds_no_exclusion_and_a_show_no_list_added_has_none_to_add()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var listed = await ApiScenes.ShowOnDiskAsync(app, "Silo", 2023);
        var source = await AddListAsync(app, listed.LibraryId);
        await RecordOriginAsync(app, source, listed.SeriesId, "Silo", 2023);
        var unlisted = await ApiScenes.ShowOnDiskAsync(app, "Andor", 2022, listed.LibraryId);

        var clear = await RemoveAsync(app, listed.SeriesId, addImportListExclusion: false);
        var ticked = await RemoveAsync(app, unlisted.SeriesId, addImportListExclusion: true);

        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ticked.StatusCode);
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/exclusions?mediaType=tv")).EnumerateArray());
        Assert.True(File.Exists(listed.FilePath));
        Assert.True(File.Exists(unlisted.FilePath));
    }

    private static Task<HttpResponseMessage> RemoveAsync(ApplicationTestHost app, string seriesId, bool addImportListExclusion)
        => app.Client.PostAsJsonAsync("/api/series/bulk", new { seriesIds = new[] { seriesId }, operation = "remove", addImportListExclusion });

    private static async Task<IntakeSourceItem> AddListAsync(ApplicationTestHost app, string libraryId)
        => await app.Services.GetRequiredService<IIntakeRepository>().CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest("Trending shows", "trakt", "https://lists.example.test/shows", "tv", libraryId, null, null, null, null, null, null, null, null, false, true),
            CancellationToken.None);

    private static Task RecordOriginAsync(ApplicationTestHost app, IntakeSourceItem source, string seriesId, string title, int year)
        => app.Services.GetRequiredService<IIntakeRepository>().RecordIntakeTitleOriginAsync(
            new CreateIntakeTitleOriginRequest(source.Id, source.Name, source.Provider, "tv", seriesId, $"{title.ToLowerInvariant()}-{year}", title, year, null),
            CancellationToken.None);
}
