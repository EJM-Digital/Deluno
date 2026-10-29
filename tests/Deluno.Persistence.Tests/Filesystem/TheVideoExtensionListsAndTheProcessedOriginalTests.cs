using System.Reflection;
using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The original a processed file is compared with, found inside a hand-off folder on a real folder, by the one shared
/// video-extension list (#1409; VideoExtensionListsTests keeps every code path on that list).
/// </summary>
public sealed class TheVideoExtensionListsAndTheProcessedOriginalTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-processed-original-" + Guid.NewGuid().ToString("N"));

    public TheVideoExtensionListsAndTheProcessedOriginalTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
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
        // Bigger than the film but not a video: a disc image is never the original (#1409).
        Write(Path.Combine("Film.2020.1080p", "Film.2020.1080p.iso"), 8000);

        Assert.Equal(film, Resolve(Path.Combine(_folder, "Film.2020.1080p")));

        // A folder holding only a disc image has no original to compare with.
        Write(Path.Combine("Disc.Only", "Disc.iso"), 4000);
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
