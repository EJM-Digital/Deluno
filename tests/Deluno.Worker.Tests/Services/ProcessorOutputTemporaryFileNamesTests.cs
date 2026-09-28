using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #953: the processor contract requires a drop-folder processor to write
/// under a temporary name and rename to a real video extension once done.
/// A ".partial" file sitting in a hand-off's own output subfolder must never
/// be picked up as the match -- only the rename, to a name
/// <see cref="Deluno.Filesystem.SupportedVideoExtensions"/> recognises,
/// releases it.
/// </summary>
public sealed class ProcessorOutputTemporaryFileNamesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"deluno-processor-output-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_still_writing_temporary_named_file_is_not_matched()
    {
        var sourcePath = @"D:\Downloads\Sintel.2010.1080p";
        var handoffFolder = Directory.CreateDirectory(Path.Combine(_root, "Sintel.2010.1080p")).FullName;
        var partial = Path.Combine(handoffFolder, "Sintel.2010.1080p.mkv.partial");
        File.WriteAllText(partial, "still writing");

        var matches = WorkPlanner.FindCorrelatedProcessorOutputs(_root, sourcePath);

        Assert.Empty(matches);
    }

    [Fact]
    public void Renaming_it_to_a_supported_video_extension_releases_it()
    {
        var sourcePath = @"D:\Downloads\Sintel.2010.1080p";
        var handoffFolder = Directory.CreateDirectory(Path.Combine(_root, "Sintel.2010.1080p")).FullName;
        var partial = Path.Combine(handoffFolder, "Sintel.2010.1080p.mkv.partial");
        File.WriteAllBytes(partial, [1, 2, 3, 4]);

        // Nothing yet: still under its temporary name.
        Assert.Empty(WorkPlanner.FindCorrelatedProcessorOutputs(_root, sourcePath));

        var finished = Path.Combine(handoffFolder, "Sintel.2010.1080p.mkv");
        File.Move(partial, finished);
        File.SetLastWriteTimeUtc(finished, DateTime.UtcNow.AddMinutes(-1));

        var matches = WorkPlanner.FindCorrelatedProcessorOutputs(_root, sourcePath);

        Assert.Single(matches);
        Assert.Equal(finished, matches[0], ignoreCase: true);
    }
}
