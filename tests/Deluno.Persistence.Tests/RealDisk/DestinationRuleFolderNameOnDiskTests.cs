using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The "Folder name format" box of a destination rule: its tokens are filled from the title and the file really lands in the
/// folder they name, inside the rule's own folder, on a real disk.
/// </summary>
public sealed class DestinationRuleFolderNameOnDiskTests
{
    private const string Release = "Sintel.2010.WEB.1080p";

    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static Task AddRuleAsync(RealDiskLibrary world, string root, string? template)
        => world.App.Services.GetRequiredService<ILibrariesRepository>().CreateDestinationRuleAsync(
            new CreateDestinationRuleRequest("Kids films", "movies", "tag", "kids", root, template, 1, true), CancellationToken.None);

    [Fact]
    public async Task A_rule_folder_format_fills_title_year_genre_and_tag_and_the_file_lands_in_that_folder_inside_the_rule_folder()
    {
        await using var world = await StartWorldAsync();
        var ruleRoot = Path.Combine(world.App.DataRoot, "kids");
        await AddRuleAsync(world, ruleRoot, "{Genre}/{Tag}/{Title} ({Year})");
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv");
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");
        var request = RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: dispatch);
        request = request with { Preview = request.Preview with { Genres = ["Animation"], Tags = ["kids"] } };

        var result = await world.ImportAsync(request);

        Assert.True(result.Succeeded, result.Message);
        var landed = result.Response!.Preview.DestinationPath;
        var folder = Path.GetDirectoryName(landed)!;
        Assert.Equal(Path.Combine(ruleRoot, "Animation", "kids", "Sintel (2010)"), folder);
        Assert.True(File.Exists(landed), "the file is on disk in the rule's folder");
        Assert.Empty(RealDiskLibrary.FilesUnder(world.Root));
    }

    [Fact]
    public async Task A_rule_with_no_folder_format_uses_the_standard_folder_naming_inside_the_rule_folder()
    {
        await using var world = await StartWorldAsync();
        var ruleRoot = Path.Combine(world.App.DataRoot, "kids");
        await AddRuleAsync(world, ruleRoot, null);
        var source = world.WriteDownload(Release + ".mkv");
        var request = RealDiskLibrary.Request(source, "Sintel", 2010);

        var preview = await world.PreviewAsync(request.Preview with { Tags = ["kids"] });

        Assert.StartsWith(ruleRoot, preview.DestinationPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sintel", Path.GetDirectoryName(preview.DestinationPath));
        Assert.Contains("2010", Path.GetDirectoryName(preview.DestinationPath));
    }
}
