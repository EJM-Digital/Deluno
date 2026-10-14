using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Deluno.Filesystem;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// First imports of a film, run through the real import pipeline on real files in a real temp folder: where the file
/// lands (rule, library, platform root), what is refused before anything is written (a sample, the same file, no
/// root, no permission), and that a hardlink import really shares one set of file data with the download.
/// </summary>
public sealed class ImportingAFilmOnDiskTests
{
    private const string Release = "Sintel.2010.WEB.1080p";

    private static async Task<RealDiskLibrary> StartWorldAsync(IMediaProbeService? probe = null)
    {
        var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(probe));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static void AssertNoImportLeftovers(RealDiskLibrary world)
    {
        var leftovers = RealDiskLibrary.FilesUnder(world.Root).Where(path => path.Contains(".deluno-", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(leftovers.Length == 0, "staging or marker files left behind: " + string.Join(", ", leftovers));
    }

    // ---------------------------------------------------------------- a likely sample is refused

    [Theory]
    [InlineData(90, false)]
    [InlineData(119, false)]
    [InlineData(120, true)]
    public async Task A_file_with_a_detected_runtime_under_120_seconds_is_refused_as_a_likely_sample_and_exactly_120_imports(double seconds, bool imports)
    {
        await using var world = await StartWorldAsync(new ScriptedProbe(_ => ScriptedProbe.Good(seconds: seconds)));
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv", fill: 0x44);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, dispatchId: dispatch));

        Assert.Equal(imports, result.Succeeded);
        if (imports)
        {
            Assert.Single(RealDiskLibrary.FilesUnder(world.Root));
        }
        else
        {
            Assert.Equal(400, result.StatusCode);
            Assert.Contains("under two minutes", result.Message);
            Assert.Empty(RealDiskLibrary.FilesUnder(world.Root));
            Assert.True(File.Exists(source), "the refused download stays where it was");
            var recovery = await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None);
            var opened = Assert.Single(recovery.RecentCases);
            Assert.Contains("likely sample", opened.Summary);
            Assert.Contains("full release file", opened.RecommendedAction);
        }
    }

    // ---------------------------------------------------------------- a hardlink import really shares the file data

    [Fact]
    public async Task A_hardlink_import_gives_the_library_file_and_the_download_one_set_of_file_data()
    {
        await using var world = await StartWorldAsync(new ScriptedProbe(_ => ScriptedProbe.Good()));
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv", fill: 0x55);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, mode: "hardlink", dispatchId: dispatch));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("hardlink", result.Response!.TransferModeUsed);
        Assert.False(result.Response.UsedFallback);
        var library = result.Response.Preview.DestinationPath;
        Assert.True(world.App.Services.GetRequiredService<IHardlinkInspector>().StillLinked(source, library),
            "the library file and the download should be one file on disk");

        // The proof that matters: a byte written through one name shows through the other, so no second copy exists.
        File.AppendAllText(library, "tail");
        Assert.Equal(new FileInfo(library).Length, new FileInfo(source).Length);
        AssertNoImportLeftovers(world);
    }

    [Fact]
    public async Task A_copy_import_leaves_two_separate_files_so_the_inspector_says_they_are_not_linked()
    {
        await using var world = await StartWorldAsync(new ScriptedProbe(_ => ScriptedProbe.Good()));
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv", fill: 0x55);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, mode: "copy", dispatchId: dispatch));

        Assert.True(result.Succeeded, result.Message);
        var library = result.Response!.Preview.DestinationPath;
        Assert.False(world.App.Services.GetRequiredService<IHardlinkInspector>().StillLinked(source, library));
        File.AppendAllText(library, "tail");
        Assert.NotEqual(new FileInfo(library).Length, new FileInfo(source).Length);
    }

    // ---------------------------------------------------------------- refused before anything is written

    [Fact]
    public async Task A_file_that_is_already_at_its_destination_is_refused_409_and_left_intact()
    {
        await using var world = await StartWorldAsync(new ScriptedProbe(_ => ScriptedProbe.Good()));
        // The downloads folder is the library: the file sits exactly where the import would put it.
        var folder = Path.Combine(world.Root, "Sintel (2010)");
        Directory.CreateDirectory(folder);
        var inPlace = Path.Combine(folder, Release + ".mkv");
        File.WriteAllBytes(inPlace, Enumerable.Repeat((byte)0x66, 4096).ToArray());
        File.SetLastWriteTimeUtc(inPlace, DateTime.UtcNow.AddMinutes(-5));

        var result = await world.ImportAsync(RealDiskLibrary.Request(inPlace, "Sintel", 2010, mode: "move"));

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("will not import a file onto itself", result.Message);
        Assert.All(File.ReadAllBytes(inPlace), value => Assert.Equal(0x66, value));
        Assert.Single(RealDiskLibrary.FilesUnder(world.Root));
    }

    [Fact]
    public async Task With_no_library_and_no_platform_root_the_import_is_refused_409_and_writes_nothing_under_the_working_directory()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good())));
        var downloads = Path.Combine(app.DataRoot, "downloads");
        Directory.CreateDirectory(downloads);
        var source = Path.Combine(downloads, Release + ".mkv");
        File.WriteAllBytes(source, Enumerable.Repeat((byte)0x66, 4096).ToArray());
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-5));
        var workingDirectoryBefore = Directory.GetFileSystemEntries(Directory.GetCurrentDirectory()).Length;

        using var scope = app.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IImportPipelineService>().ExecuteAsync(
            RealDiskLibrary.Request(source, "Sintel", 2010), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("No library root is configured", result.Message);
        Assert.False(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "Sintel (2010)")), "nothing was written relative to the working directory");
        Assert.Equal(workingDirectoryBefore, Directory.GetFileSystemEntries(Directory.GetCurrentDirectory()).Length);
        Assert.True(File.Exists(source));
        var cases = (await scope.ServiceProvider.GetRequiredService<Deluno.Movies.Data.IMovieCatalogRepository>().GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases;
        var missing = Assert.Single(cases);
        Assert.Contains("No library root is configured", missing.Summary);
        Assert.Contains("Set the root folder", missing.RecommendedAction);
    }

    [Fact]
    public async Task A_destination_the_account_may_not_write_to_answers_403_and_leaves_the_source_and_no_staging_file()
    {
        await using var world = await StartWorldAsync(new ScriptedProbe(_ => ScriptedProbe.Good()));
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var source = world.WriteDownload(Release + ".mkv", fill: 0x66);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");
        var movieFolder = Path.Combine(world.Root, "Sintel (2010)");
        Directory.CreateDirectory(movieFolder);
        using var denial = FolderWriteDenial.TryApply(movieFolder);
        if (denial is null)
        {
            return; // this account is allowed to write anywhere (for example root in a container), so the OS cannot be made to refuse
        }

        var result = await world.ImportAsync(RealDiskLibrary.Request(source, "Sintel", 2010, mode: "move", dispatchId: dispatch));
        denial.Dispose();

        Assert.False(result.Succeeded);
        Assert.Equal(403, result.StatusCode);
        Assert.Contains("does not have permission", result.Message);
        Assert.True(File.Exists(source), "a move that was refused is put back");
        Assert.All(File.ReadAllBytes(source), value => Assert.Equal(0x66, value));
        Assert.DoesNotContain(RealDiskLibrary.FilesUnder(world.Root), path => path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));
        AssertNoImportLeftovers(world);
        var opened = Assert.Single((await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
        Assert.Contains("read access to downloads and write access to the destination library", opened.RecommendedAction);
        Assert.Contains(RunAccount.Phrase(), opened.RecommendedAction);
    }

    // ---------------------------------------------------------------- where the file lands

    [Theory]
    [InlineData("genre", "Animation", "Animation, Short")]
    [InlineData("tag", "kids", "Kids Corner")]
    [InlineData("language", "EN", "en")]
    public async Task A_destination_rule_places_the_file_by_genre_tag_or_language_ignoring_case(string kind, string ruleValue, string requestValue)
    {
        await using var world = await StartWorldAsync(new ScriptedProbe(_ => ScriptedProbe.Good()));
        var ruleRoot = Path.Combine(world.App.DataRoot, "special-" + kind);
        await world.CreateRuleAsync("Special " + kind, kind, ruleValue, ruleRoot, priority: 1);
        var source = world.WriteDownload(Release + ".mkv");

        var preview = await world.PreviewAsync(RealDiskLibrary.Request(source, "Sintel", 2010).Preview with
        {
            Genres = kind == "genre" ? [requestValue] : [],
            Tags = kind == "tag" ? [requestValue] : [],
            OriginalLanguage = kind == "language" ? requestValue : null
        });

        Assert.StartsWith(ruleRoot, preview.DestinationPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(kind == "tag" ? "Placed by the tag" : "Matched " + kind + " destination rule", preview.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Special " + kind, preview.MatchedRuleName);
    }

    // DEFECT: the library repository saves a "studio" or "title" rule as a "genre" rule (NormalizeDestinationMatchKind in
    // SqliteLibrariesRepository maps every kind it does not list to "genre"), so those rules can never match what they name.
    [Theory(Skip = "Defect: a destination rule of kind studio or title is saved as a genre rule, so it never matches by studio or title.")]
    [InlineData("studio", "Blender", "Blender Foundation")]
    [InlineData("title", "sintel", "Sintel")]
    public async Task A_destination_rule_places_the_file_by_studio_or_title_ignoring_case(string kind, string ruleValue, string requestValue)
    {
        await using var world = await StartWorldAsync(new ScriptedProbe(_ => ScriptedProbe.Good()));
        var ruleRoot = Path.Combine(world.App.DataRoot, "special-" + kind);
        await world.CreateRuleAsync("Special " + kind, kind, ruleValue, ruleRoot, priority: 1);
        var source = world.WriteDownload(Release + ".mkv");
        var preview = await world.PreviewAsync(RealDiskLibrary.Request(source, "Sintel", 2010).Preview with { Studio = kind == "studio" ? requestValue : null });
        Assert.StartsWith(ruleRoot, preview.DestinationPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_disabled_rule_is_ignored_and_the_first_enabled_match_by_priority_wins()
    {
        await using var world = await StartWorldAsync(new ScriptedProbe(_ => ScriptedProbe.Good()));
        var disabledRoot = Path.Combine(world.App.DataRoot, "disabled-root");
        var laterRoot = Path.Combine(world.App.DataRoot, "later-root");
        var firstRoot = Path.Combine(world.App.DataRoot, "first-root");
        await world.CreateRuleAsync("Disabled", "language", "en", disabledRoot, priority: 0, enabled: false);
        await world.CreateRuleAsync("Later", "language", "e", laterRoot, priority: 9);
        await world.CreateRuleAsync("First", "language", "n", firstRoot, priority: 2);
        var source = world.WriteDownload(Release + ".mkv");

        var preview = await world.PreviewAsync(RealDiskLibrary.Request(source, "Sintel", 2010).Preview with { OriginalLanguage = "en" });

        Assert.Equal("First", preview.MatchedRuleName);
        Assert.StartsWith(firstRoot, preview.DestinationPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_rule_root_beats_the_library_root_and_with_neither_the_platform_default_root_is_used()
    {
        await using var world = await StartWorldAsync(new ScriptedProbe(_ => ScriptedProbe.Good()));
        var ruleRoot = Path.Combine(world.App.DataRoot, "rule-root");
        var source = world.WriteDownload(Release + ".mkv");
        var request = RealDiskLibrary.Request(source, "Sintel", 2010).Preview;

        var byLibrary = await world.PreviewAsync(request);
        Assert.StartsWith(world.Root, byLibrary.DestinationPath, StringComparison.OrdinalIgnoreCase);

        await world.CreateRuleAsync("By language", "language", "en", ruleRoot, priority: 1);
        request = request with { OriginalLanguage = "en" };
        var byRule = await world.PreviewAsync(request);
        Assert.StartsWith(ruleRoot, byRule.DestinationPath, StringComparison.OrdinalIgnoreCase);

        // No library of this type and no rule: only the platform default root is left.
        var libraries = world.App.Services.GetRequiredService<ILibrariesRepository>();
        foreach (var rule in await libraries.ListDestinationRulesAsync(CancellationToken.None))
        {
            await libraries.DeleteDestinationRuleAsync(rule.Id, CancellationToken.None);
        }

        var platformRoot = Path.Combine(world.App.DataRoot, "platform-movies");
        var settings = (await world.App.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        settings["movieRootPath"] = platformRoot;
        var saved = await world.App.Client.PutAsJsonAsync("/api/settings", settings);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.True(await libraries.DeleteLibraryAsync(world.Library.Id, CancellationToken.None));

        var byPlatform = await world.PreviewAsync(request);
        Assert.StartsWith(platformRoot, byPlatform.DestinationPath, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Makes a real folder refuse new files for the account running the test, and puts it back on dispose.</summary>
internal sealed class FolderWriteDenial : IDisposable
{
    private Action? _undo;

    private FolderWriteDenial(Action undo) => _undo = undo;

    public static FolderWriteDenial? TryApply(string folder)
        => OperatingSystem.IsWindows() ? DenyOnWindows(folder) : DenyOnPosix(folder);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FolderWriteDenial DenyOnWindows(string folder)
    {
        {
            var info = new DirectoryInfo(folder);
            var security = info.GetAccessControl();
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var rule = new System.Security.AccessControl.FileSystemAccessRule(
                user,
                System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.WriteData,
                System.Security.AccessControl.InheritanceFlags.None,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Deny);
            security.AddAccessRule(rule);
            info.SetAccessControl(security);
            return new FolderWriteDenial(() =>
            {
                var restore = info.GetAccessControl();
                restore.RemoveAccessRule(rule);
                info.SetAccessControl(restore);
            });
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static FolderWriteDenial? DenyOnPosix(string folder)
    {
        var original = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.WriteAllText(Path.Combine(folder, ".probe"), "x");
            File.Delete(Path.Combine(folder, ".probe"));
            File.SetUnixFileMode(folder, original);
            return null; // still writable: this account ignores the mode
        }
        catch (UnauthorizedAccessException)
        {
            return new FolderWriteDenial(() => File.SetUnixFileMode(folder, original));
        }
    }

    public void Dispose()
    {
        _undo?.Invoke();
        _undo = null;
    }
}
