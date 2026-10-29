using System.Reflection;
using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The shared video-extension list and the lists that still differ from it, pinned exactly, so a new difference
/// cannot creep in unseen (a .webm or .iso counted as a main video by one place and ignored by another); and the
/// original a processed file is compared with, found inside a hand-off folder on a real folder.
/// </summary>
public sealed class TheVideoExtensionListsAndTheProcessedOriginalTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-processed-original-" + Guid.NewGuid().ToString("N"));

    public TheVideoExtensionListsAndTheProcessedOriginalTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly Assembly Platform = typeof(Deluno.Platform.RecycleBinService).Assembly;

    private static IReadOnlyCollection<string> ListOf(Type type)
    {
        var field = type.GetField("VideoExtensions", BindingFlags.NonPublic | BindingFlags.Static)!;
        return ((IEnumerable<string>)field.GetValue(null)!).Select(extension => extension.ToLowerInvariant()).ToArray();
    }

    private static string[] Sorted(IEnumerable<string> values) => [.. values.OrderBy(value => value, StringComparer.Ordinal)];

    [Fact]
    public void Every_other_video_extension_list_differs_from_the_shared_one_only_in_the_documented_ways()
    {
        var shared = SupportedVideoExtensions.Extensions.Select(extension => extension.ToLowerInvariant()).ToArray();
        Assert.Equal(Sorted([".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts"]), Sorted(shared));

        var lists = new (string Name, Type Type, string[] Extra, string[] Missing)[]
        {
            ("MovieFilePathSafety", typeof(MovieFilePathSafety), [".webm"], []),
            ("MovieFilesEndpoint", typeof(MovieFilesEndpointRouteBuilderExtensions), [".webm"], []),
            ("ProcessorMainVideoJudgement", Platform.GetType("Deluno.Platform.Processing.ProcessorMainVideoJudgement")!, [".flv", ".iso", ".mpeg", ".mpg", ".webm"], []),
            ("ProcessorOutputPathPolicy", Platform.GetType("Deluno.Platform.Processing.ProcessorOutputPathPolicy")!, [".iso", ".mpeg", ".mpg"], [".m2ts"]),
            ("ProcessorOutputLayout", Platform.GetType("Deluno.Platform.Processing.ProcessorOutputLayout")!, [".iso", ".mpeg", ".mpg"], [".m2ts"]),
        };

        foreach (var (name, type, extra, missing) in lists)
        {
            Assert.NotNull(type);
            var list = ListOf(type);
            Assert.True(
                Sorted(list.Except(shared)).SequenceEqual(Sorted(extra)),
                $"{name} adds {string.Join(" ", list.Except(shared))}; expected {string.Join(" ", extra)}");
            Assert.True(
                Sorted(shared.Except(list)).SequenceEqual(Sorted(missing)),
                $"{name} leaves out {string.Join(" ", shared.Except(list))}; expected {string.Join(" ", missing)}");
        }
    }

    private static string? Resolve(string processedFrom)
        => (string?)typeof(ImportPipelineService)
            .GetMethod("ResolveProcessedSourceFile", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [processedFrom]);

    private string Write(string relative, int size)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public void The_original_a_processed_file_is_compared_with_is_the_biggest_listed_video_inside_the_hand_off_folder()
    {
        var film = Write(Path.Combine("Film.2020.1080p", "Film.2020.1080p.mkv"), 5000);
        Write(Path.Combine("Film.2020.1080p", "Extras", "Making.Of.mp4"), 3000);
        Write(Path.Combine("Film.2020.1080p", "Film.nfo"), 9000);
        // Bigger than the film but not an extension the comparison reads.
        Write(Path.Combine("Film.2020.1080p", "Film.2020.1080p.m2ts"), 8000);

        Assert.Equal(film, Resolve(Path.Combine(_folder, "Film.2020.1080p")));

        // A folder holding only an unlisted extension has no original to compare with.
        Write(Path.Combine("Disc.Only", "Disc.m2ts"), 4000);
        Assert.Null(Resolve(Path.Combine(_folder, "Disc.Only")));

        // A single-file hand-off is itself; a source that is gone has nothing.
        Assert.Equal(film, Resolve(film));
        Assert.Null(Resolve(Path.Combine(_folder, "Gone")));

        foreach (var extension in new[] { "mkv", "mp4", "avi", "mov", "m4v", "ts" })
        {
            var only = Write(Path.Combine("One." + extension, "Only." + extension.ToUpperInvariant()), 10);
            Assert.Equal(only, Resolve(Path.Combine(_folder, "One." + extension)));
        }
    }
}
