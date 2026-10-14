using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Platform.Tests;

/// <summary>
/// #1306: one shared helper decides whether a path that came from outside stays inside its folder, and
/// every place that joins such a path uses it. Each place is pinned here or beside its own tests.
/// </summary>
public sealed class SafePathTests
{
    private sealed record Mapping(string Source, string Target);

    [Theory]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/a/./b//c/", "/a/b/c")]
    [InlineData("D:\\media\\..\\x\\.", "D:/x")]
    [InlineData("\\\\nas\\share\\a\\..\\b", "//nas/share/b")]
    [InlineData("a/b/../../c", "c")]
    public void A_path_is_normalised_with_dots_repeats_and_mixed_separators_removed(string path, string expected)
        => Assert.Equal(expected, SafePath.Normalize(path));

    [Theory]
    [InlineData("/a/../..")]
    [InlineData("../x")]
    [InlineData("D:\\..\\x")]
    [InlineData("\\\\nas\\share\\..\\..\\x")]
    [InlineData("")]
    [InlineData(null)]
    public void A_path_that_climbs_above_its_own_start_cannot_be_normalised(string? path)
        => Assert.Null(SafePath.Normalize(path));

    [Theory]
    [InlineData("/lib", "/lib/film/a.mkv", true)]
    [InlineData("/lib", "/lib/film/../a.mkv", true)]
    [InlineData("/lib", "/lib/../etc/passwd", false)]
    [InlineData("/lib", "/lib2/a.mkv", false)]
    [InlineData("/lib", "/lib/../lib2/a.mkv", false)]
    [InlineData("/lib/", "\\lib\\film", true)]
    [InlineData("/lib", "/lib", false)]
    [InlineData("D:\\Media", "d:/media/Film", true)]
    public void A_path_is_inside_a_root_only_when_it_lands_below_it_after_normalising(string root, string path, bool expected)
        => Assert.Equal(expected, SafePath.IsInside(root, path, comparison: StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void The_root_itself_counts_only_when_asked()
    {
        Assert.False(SafePath.IsInside("/lib", "/lib/"));
        Assert.True(SafePath.IsInside("/lib", "/lib/", allowEqual: true));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("a\\..\\..\\x")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows")]
    public void A_part_that_climbs_out_or_is_rooted_is_refused_with_a_plain_reason(string relative)
    {
        Assert.False(SafePath.TryResolveUnder("/lib", relative, out var result, out var reason));
        Assert.Equal(string.Empty, result);
        Assert.Equal(SafePath.OutsideRootReason, reason);
    }

    [Fact]
    public void A_clean_part_is_joined_under_the_root()
    {
        Assert.True(SafePath.TryResolveUnder("/lib", "a/./b/../c", out var result, out _));
        Assert.Equal("/lib/a/c", result);
    }

    [Theory]
    [InlineData("/lib/film.mkv", true)]
    [InlineData("D:\\lib\\film.mkv", true)]
    [InlineData("\\\\nas\\lib\\film.mkv", true)]
    [InlineData("/lib/../etc/x", false)]
    [InlineData("/lib/./x", false)]
    [InlineData("lib/film.mkv", false)]
    [InlineData("", false)]
    public void The_file_changed_route_takes_only_a_full_path_without_dot_segments(string path, bool expected)
        => Assert.Equal(expected, SafePath.IsPlainFullPath(path));

    [Fact]
    public void Mapping_refuses_a_path_that_climbs_out_of_the_target_root_and_says_why()
    {
        var mappings = new[] { new Mapping("/src/media", "/dst/media") };
        var result = PathMappingTranslator.TryTranslate(
            "/src/media/../../etc/passwd", mappings, _ => true, m => m.Source, m => m.Target, _ => 0, out var reason);

        Assert.Null(result);
        Assert.Equal(SafePath.OutsideRootReason, reason);
    }

    [Fact]
    public void Mapping_tidies_dots_that_stay_inside_and_leaves_clean_paths_alone()
    {
        var mappings = new[] { new Mapping("/src/media", "/dst/media") };
        Assert.Equal(
            Path.Combine("/dst/media", "a", "c.mkv"),
            PathMappingTranslator.Translate("/src/media/a/./b/../c.mkv", mappings, _ => true, m => m.Source, m => m.Target, _ => 0));
        Assert.Equal(
            Path.Combine("/dst/media", "a", "c.mkv"),
            PathMappingTranslator.Translate("/src/media/a//c.mkv", mappings, _ => true, m => m.Source, m => m.Target, _ => 0));
    }

    [Fact]
    public void A_processor_path_that_climbs_out_is_refused_in_both_directions()
    {
        var item = new Deluno.Platform.Contracts.ProcessorPathMappingItem(
            "1", "p", "/deluno/media", "/proc/media", true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        Assert.Null(ProcessorPathMappingTranslator.ToDeluno("/proc/media/../../etc/passwd", [item]));
        Assert.Null(ProcessorPathMappingTranslator.ToProcessor("/deluno/media/../../etc/passwd", [item]));
    }

    [Fact]
    public void A_processor_source_that_climbs_has_no_output_paths()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "deluno-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputRoot);
        try
        {
            Assert.Empty(ProcessorOutputLayout.ExistingOutputPaths(outputRoot, "/watch/../../etc/passwd", "/watch"));
            Assert.Empty(ProcessorOutputLayout.ExistingOutputPaths(outputRoot, "/watch/x/../../../etc/passwd", "/watch"));
        }
        finally
        {
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task The_recycle_bin_will_not_take_a_path_that_climbs_out_of_the_library()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"deluno-safepath-{Guid.NewGuid():N}");
        var libraryRoot = Path.Combine(sandbox, "library");
        var outside = Path.Combine(sandbox, "leave-alone.mkv");
        try
        {
            Directory.CreateDirectory(libraryRoot);
            File.WriteAllText(outside, "outside");
            var service = new RecycleBinService(
                Options.Create(new StoragePathOptions { DataRoot = Path.Combine(sandbox, "data") }),
                TimeProvider.System,
                NullLogger<RecycleBinService>.Instance);
            var now = DateTimeOffset.UtcNow;
            var library = new LibraryItem(
                "library-1", "Test library", "movies", "main", libraryRoot, null, null, null, null,
                true, true, "direct", null, null, 0, "block",
                false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);

            var climbing = Path.Combine(libraryRoot, "..", "leave-alone.mkv");
            var stored = await service.StoreRemovedFileAsync(library, climbing, owner: null, CancellationToken.None);

            Assert.Null(stored);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }
}
