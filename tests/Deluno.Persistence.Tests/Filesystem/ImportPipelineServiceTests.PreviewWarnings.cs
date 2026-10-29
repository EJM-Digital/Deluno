using System.Reflection;
using Deluno.Filesystem;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Deluno.Movies.Data;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The plain-words warnings a preview gives before anything is placed: the file itself (extension, not visible,
/// written a moment ago, already in the library) and what the probe found, each on a real file in a temp folder.
/// </summary>
public sealed partial class ImportPipelineServiceTests
{
    private sealed class FixedProbeService(MediaProbeInfo info) : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken) => Task.FromResult(info);
    }

    private static MediaVideoStreamInfo OneVideoStream()
        => new(0, "h264", "High", 1920, 1080, "yuv420p", 23.976, 10_000_000, "eng");

    private async Task<(TestStorage Storage, ImportPipelineService Service, string Downloads, string MovieRoot)> PreviewSceneAsync(
        IMediaProbeService? probe = null)
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloads = Path.Combine(storage.DataRoot, "downloads");
        var movieRoot = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(movieRoot);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRoot, downloads);
        await CreateMovieLibraryAsync(libraries, movieRoot, downloads);
        var service = CreateService(storage, timeProvider, platform, libraries,
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider), mediaProbeService: probe);
        return (storage, service, downloads, movieRoot);
    }

    private static ImportPreviewRequest MoviePreview(string sourcePath)
        => new(sourcePath, null, "movies", "Arrival", 2016, [], [], null, "en", "tt2543164");

    private static async Task<string> WriteDownloadAsync(string folder, string name, int ageMinutes = 5)
    {
        var path = Path.Combine(folder, name);
        await File.WriteAllBytesAsync(path, Enumerable.Range(0, 4096).Select(v => (byte)(v % 251)).ToArray());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-ageMinutes));
        return path;
    }

    [Fact]
    public async Task A_preview_warns_in_plain_words_when_the_file_is_not_a_video_not_visible_new_or_already_in_the_library()
    {
        var (storage, service, downloads, _) = await PreviewSceneAsync();
        using var owned = storage;

        // An extension that is not a video.
        var text = await WriteDownloadAsync(downloads, "Arrival.2016.1080p.txt");
        var notVideo = await service.PreviewAsync(MoviePreview(text), CancellationToken.None);
        Assert.Contains("This file extension is not configured as an importable video file.", notVideo.Warnings);
        Assert.Contains("Attention: This file extension is not configured as an importable video file.", notVideo.DecisionSteps);

        // A source Deluno cannot see: the hint names Docker mounts, UNC, mapped drives and the account Deluno runs as.
        var missing = await service.PreviewAsync(MoviePreview(Path.Combine(downloads, "Gone.2016.1080p.mkv")), CancellationToken.None);
        var notVisible = Assert.Single(missing.Warnings, w => w.StartsWith("Source file is not visible to Deluno."));
        Assert.Contains("Docker mounts, UNC access, mapped drives", notVisible);
        Assert.Contains("permissions of", notVisible);
        Assert.Contains("Attention: " + notVisible, missing.DecisionSteps);
        Assert.False(missing.SourceExists);

        // A file written in the last 30 seconds.
        var fresh = await WriteDownloadAsync(downloads, "Arrival.2016.WEB.1080p.mkv", ageMinutes: 0);
        var recent = await service.PreviewAsync(MoviePreview(fresh), CancellationToken.None);
        Assert.Contains(recent.Warnings, w => w.StartsWith("Source was modified recently."));

        // A settled file whose destination is already in the library.
        var settled = await WriteDownloadAsync(downloads, "Arrival.2016.BluRay.1080p.mkv");
        var clear = await service.PreviewAsync(MoviePreview(settled), CancellationToken.None);
        Assert.DoesNotContain(clear.Warnings, w => w.StartsWith("Destination already exists."));
        Directory.CreateDirectory(clear.DestinationFolder);
        await File.WriteAllBytesAsync(clear.DestinationPath, [1, 2, 3]);
        var taken = await service.PreviewAsync(MoviePreview(settled), CancellationToken.None);
        Assert.Contains("Destination already exists. Import will be blocked unless overwrite is enabled.", taken.Warnings);
        Assert.Contains("Destination: target file already exists and needs overwrite approval.", taken.DecisionSteps);

        // The source is the very file at the destination.
        var same = await service.PreviewAsync(MoviePreview(clear.DestinationPath), CancellationToken.None);
        Assert.Contains("Source and destination resolve to the same file. Deluno will block this import.", same.Warnings);
    }

    [Fact]
    public void The_hardlink_and_drive_warnings_name_two_filesystems_and_two_drive_roots()
    {
        var build = typeof(ImportPipelineService).GetMethod("BuildImportWarnings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var source = OperatingSystem.IsWindows() ? @"C:\downloads\Film.mkv" : "/downloads/Film.mkv";
        var destination = OperatingSystem.IsWindows() ? @"D:\movies\Film (2016)\Film.mkv" : "/movies/Film (2016)/Film.mkv";

        var warnings = (IReadOnlyList<string>)build.Invoke(null, [source, destination, false, false, false, true])!;
        Assert.Contains("Hardlink is unlikely because source and destination appear to be on different filesystems. Copy fallback may be required.", warnings);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(@"Source root C:\ differs from destination root D:\.", warnings);
        }

        var same = (IReadOnlyList<string>)build.Invoke(null, [source, source.Replace("downloads", "movies"), false, false, true, true])!;
        Assert.DoesNotContain(same, w => w.StartsWith("Hardlink is unlikely"));
    }

    [Fact]
    public async Task A_preview_adds_the_probe_warning_and_decision_step_for_each_thing_the_probe_found()
    {
        async Task<ImportPreviewResponse> PreviewWithAsync(MediaProbeInfo info)
        {
            var (storage, service, downloads, _) = await PreviewSceneAsync(new FixedProbeService(info));
            using var owned = storage;
            var path = await WriteDownloadAsync(downloads, "Arrival.2016.WEB.1080p.mkv");
            return await service.PreviewAsync(MoviePreview(path), CancellationToken.None);
        }

        var noVideo = await PreviewWithAsync(new MediaProbeInfo("succeeded", "test", null, 7200, "matroska", 1, [], [], []));
        Assert.Contains("ffprobe did not find a video stream in this file.", noVideo.Warnings);
        Assert.Contains(noVideo.DecisionSteps, s => s.StartsWith("Probe: ffprobe detected 02:00:00, no video stream"));

        var sample = await PreviewWithAsync(new MediaProbeInfo("succeeded", "test", null, 90, "matroska", 1, [OneVideoStream()], [], []));
        Assert.Contains("Detected runtime is under two minutes. This is likely a sample.", sample.Warnings);
        Assert.Contains(sample.DecisionSteps, s => s.StartsWith("Probe: ffprobe detected 00:01:30, h264 1920x1080"));

        var unavailable = await PreviewWithAsync(new MediaProbeInfo("unavailable", "test", null, null, null, null, [], [], []));
        Assert.Contains("ffprobe is unavailable, so Deluno cannot validate streams before import.", unavailable.Warnings);
        Assert.Contains("Probe: ffprobe is unavailable, so stream validation was skipped.", unavailable.DecisionSteps);

        var failed = await PreviewWithAsync(new MediaProbeInfo("failed", "test", "Invalid data found.", null, null, null, [], [], []));
        Assert.Contains("Invalid data found.", failed.Warnings);
        Assert.Contains("Probe: ffprobe failed to parse the file, so import should be blocked until the file is verified.", failed.DecisionSteps);

        var good = await PreviewWithAsync(new MediaProbeInfo("succeeded", "test", null, 7200, "matroska", 1, [OneVideoStream()], [], []));
        Assert.DoesNotContain(good.Warnings, w => w.Contains("ffprobe") || w.Contains("sample"));
    }
}
