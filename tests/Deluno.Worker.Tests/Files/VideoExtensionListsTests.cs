using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Platform.Processing;
using Deluno.Quality;
using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Files;

/// <summary>
/// #1409: one shared list of video extensions (<see cref="VideoFileExtensions"/>). The same file was imported by one code path
/// and ignored by another because each kept its own list (.webm and .iso were in some and not in others).
/// </summary>
public sealed class VideoExtensionListsTests
{
    public static TheoryData<string> Shared()
    {
        var data = new TheoryData<string>();
        foreach (var extension in VideoFileExtensions.Extensions.Order())
        {
            data.Add(extension);
        }

        return data;
    }

    [Fact]
    public void The_shared_list_holds_every_extension_any_path_used_to_accept_except_disc_images_and_dvd_fragments()
    {
        // A disc image (.iso) and a DVD fragment (.vob) are not a film file: imports refused them before the lists were
        // joined, and still do.
        string[] used =
        [
            ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg",
            ".webm", ".flv", ".ogm", ".ogv", ".divx", ".3gp"
        ];

        Assert.Equal(used.Order(), VideoFileExtensions.Extensions.Order());
    }

    [Theory]
    [MemberData(nameof(Shared))]
    public void Every_path_that_judges_a_file_agrees_it_is_video(string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Film.2020.1080p-GRP" + extension);
            File.WriteAllBytes(file, new byte[2048]);

            Assert.True(VideoFileExtensions.Contains(file));
            Assert.True(VideoFileExtensions.Contains(extension.ToUpperInvariant()));
            Assert.True(SupportedVideoExtensions.Contains(file));
            Assert.True(WorkPlanner.IsImportableVideoFile(file));
            Assert.Equal([file], ProcessorMainVideoJudgement.OutputsAt(root));
            Assert.True(ProcessorMainVideoJudgement.Judge(file, [file], "A processor").MainVideoDelivered);
            Assert.Equal("GRP", MediaFileNameFacts.Parse("Film.2020.1080p.BluRay.x264-GRP" + extension).ReleaseGroup);
            Assert.Equal(extension, VideoFileExtensions.TrailingVideoExtension("Film.2020.1080p-GRP" + extension));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(".srt")]
    [InlineData(".nfo")]
    [InlineData(".jpg")]
    [InlineData(".txt")]
    [InlineData(".nzb")]
    public void A_file_that_is_not_video_is_judged_so_by_every_path(string extension)
    {
        var name = "Film.2020.1080p-GRP" + extension;

        Assert.False(VideoFileExtensions.Contains(name));
        Assert.False(SupportedVideoExtensions.Contains(name));
        Assert.False(WorkPlanner.IsImportableVideoFile(name));
        Assert.Null(VideoFileExtensions.TrailingVideoExtension(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Film.2020.1080p.BluRay.x264-GRP")]
    [InlineData("noextension")]
    public void A_name_with_no_video_extension_has_none_to_strip(string? name)
    {
        Assert.False(VideoFileExtensions.Contains(name));
        Assert.Null(VideoFileExtensions.TrailingVideoExtension(name));
    }

    /// <summary>Reads the sources: a list added the old way would still work and simply drift again.</summary>
    [Fact]
    public void No_source_file_keeps_its_own_list_of_video_extensions()
    {
        var root = RepositoryRoot();
        var shared = Path.Combine("src", "Deluno.Contracts", "VideoFileExtensions.cs");
        string[] probes = [".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".m2ts", ".webm", ".mpg", ".mpeg", ".flv", ".divx", ".vob"];
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path);
            if (relative.Equals(shared, StringComparison.OrdinalIgnoreCase)
                || relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            var found = probes.Count(probe => Regex.IsMatch(source, "\"" + Regex.Escape(probe) + "\""));
            if (found >= 3)
            {
                offenders.Add($"{relative} ({found} video extensions written out)");
            }
        }

        Assert.True(offenders.Count == 0, "These files keep their own video-extension list; use Deluno.Contracts.VideoFileExtensions: " + string.Join("; ", offenders));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
