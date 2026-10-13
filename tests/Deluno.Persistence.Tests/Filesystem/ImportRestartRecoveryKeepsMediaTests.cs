using System.Runtime.InteropServices;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #1259: a restart marker that is merely busy (a media server, antivirus or sync client has it open) or that is
/// truncated must never make Deluno delete the staging file. For a "move" import that file is the only copy of the
/// media. Every variation is here: copy, move and hardlink; marker busy, truncated, missing; staging present and
/// missing; and a recovery pass while an import of the same title is running.
/// </summary>
public sealed class ImportRestartRecoveryKeepsMediaTests
{
    public static TheoryData<string> Modes => new() { "move", "copy", "hardlink" };

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_marker_another_program_holds_open_never_costs_the_staging_file(string mode)
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay(mode, markerText: rig.ValidMarkerText());

        // A media server, antivirus or sync client opening the marker: a sharing violation for any other reader.
        using var held = new FileStream(scene.MarkerPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.True(File.Exists(scene.StagingPath), "the staging file is the media; a busy marker must not delete it");
        Assert.True(File.Exists(scene.MarkerPath), "a busy marker is left for the next pass");
        Assert.Equal(0, report.Recovered);
        Assert.Equal(0, report.Abandoned);
        Assert.Empty(rig.Pipeline.Imports);
        Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.StagingPath));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_truncated_marker_never_costs_the_media(string mode)
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay(mode, markerText: rig.ValidMarkerText()[..20]);

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.True(await rig.MediaIsSafeAsync(scene), "truncated marker: the staging file is kept or recycled, never deleted");
        Assert.Equal(0, report.Recovered);
        Assert.Empty(rig.Pipeline.Imports);
    }

    [Fact]
    public async Task An_empty_marker_never_costs_the_media()
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: string.Empty);

        await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.True(await rig.MediaIsSafeAsync(scene));
    }

    [Fact]
    public async Task A_marker_that_is_not_a_marker_never_costs_the_media()
    {
        using var rig = await RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: "null");

        await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.True(await rig.MediaIsSafeAsync(scene));
    }

    internal sealed record Scene(string MarkerPath, string StagingPath, string SourcePath, string DestinationPath, byte[] Bytes);

    internal sealed class StubPipeline : IImportPipelineService
    {
        public List<ImportExecuteRequest> Imports { get; } = [];

        public Task<ImportPreviewResponse> PreviewAsync(ImportPreviewRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ImportPipelineResult> ExecuteAsync(ImportExecuteRequest request, CancellationToken cancellationToken)
        {
            Imports.Add(request);
            return Task.FromResult(new ImportPipelineResult(true, 200, null, "imported"));
        }
    }

    internal sealed class RestartRig : IDisposable
    {
        private readonly TestStorage _storage;

        private RestartRig(TestStorage storage, LibraryItem library, SqliteLibrariesRepository libraries, SqliteJobStore jobs, SqliteHealthCheckRepository health, RecycleBinService bin)
        {
            _storage = storage;
            Library = library;
            Libraries = libraries;
            Jobs = jobs;
            Health = health;
            Bin = bin;
        }

        public LibraryItem Library { get; }

        public SqliteLibrariesRepository Libraries { get; }

        public SqliteJobStore Jobs { get; }

        public SqliteHealthCheckRepository Health { get; }

        public RecycleBinService Bin { get; }

        public StubPipeline Pipeline { get; } = new();

        public string Root => Library.RootPath;

        public string DownloadsPath => Path.Combine(_storage.DataRoot, "downloads");

        public static async Task<RestartRig> CreateAsync()
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-14T08:00:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

            var movieRoot = Path.Combine(storage.DataRoot, "movies");
            Directory.CreateDirectory(movieRoot);
            Directory.CreateDirectory(Path.Combine(storage.DataRoot, "downloads"));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var library = await libraries.CreateLibraryAsync(
                new CreateLibraryRequest(
                    Name: "Movies", MediaType: "movies", Purpose: "Main", RootPath: movieRoot, DownloadsPath: Path.Combine(storage.DataRoot, "downloads"),
                    QualityProfileId: null, ImportWorkflow: "standard", ProcessorName: null, ProcessorOutputPath: null,
                    ProcessorTimeoutMinutes: null, ProcessorFailureMode: null, AutoSearchEnabled: false,
                    MissingSearchEnabled: false, UpgradeSearchEnabled: false, SearchIntervalMinutes: 6, RetryDelayHours: 24,
                    MaxItemsPerRun: 25),
                CancellationToken.None);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
            var health = new SqliteHealthCheckRepository(storage.Factory, time);
            var bin = new RecycleBinService(
                Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }),
                time,
                NullLogger<RecycleBinService>.Instance);
            return new RestartRig(storage, library, libraries, jobs, health, bin);
        }

        /// <summary>
        /// What a crash leaves: a staging file and its marker. Move: the original was consumed into staging.
        /// Copy: the original is untouched. Hardlink: the original and staging are one set of file data.
        /// </summary>
        public Scene Lay(string mode, string? markerText, bool stagingPresent = true, bool writeMarker = true)
        {
            var folder = Path.Combine(Root, "Sanctuary (2024)");
            Directory.CreateDirectory(folder);
            var destinationPath = Path.Combine(folder, "Sanctuary (2024).mkv");
            var stagingPath = destinationPath + ".deluno-stage-restarttest.tmp";
            var sourcePath = Path.Combine(DownloadsPath, "Sanctuary.2024.WEB.1080p.mkv");
            var bytes = Enumerable.Range(0, 4096).Select(value => (byte)((value * 7) % 251)).ToArray();

            if (stagingPresent)
            {
                switch (mode)
                {
                    case "move":
                        File.WriteAllBytes(stagingPath, bytes);
                        break;
                    case "copy":
                        File.WriteAllBytes(sourcePath, bytes);
                        File.WriteAllBytes(stagingPath, bytes);
                        break;
                    case "hardlink":
                        File.WriteAllBytes(sourcePath, bytes);
                        Assert.True(TryCreateHardlink(sourcePath, stagingPath), "the platform must be able to hardlink for this test");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mode));
                }
            }
            else if (mode != "move")
            {
                File.WriteAllBytes(sourcePath, bytes);
            }

            var markerPath = stagingPath + ".deluno-marker.json";
            if (writeMarker)
            {
                File.WriteAllText(markerPath, markerText ?? ValidMarkerText());
            }

            return new Scene(markerPath, stagingPath, sourcePath, destinationPath, bytes);
        }

        public string ValidMarkerText()
        {
            var folder = Path.Combine(Root, "Sanctuary (2024)");
            var destinationPath = Path.Combine(folder, "Sanctuary (2024).mkv");
            return JsonSerializer.Serialize(new ImportRestartMarker(
                Path.Combine(DownloadsPath, "Sanctuary.2024.WEB.1080p.mkv"),
                destinationPath,
                destinationPath + ".deluno-stage-restarttest.tmp",
                "movies",
                "Sanctuary (2024)"));
        }

        public ImportRestartRecoveryService CreateService()
            => new(Libraries, Pipeline, Jobs, NullLogger<ImportRestartRecoveryService>.Instance);

        /// <summary>The media is still where the import left it, or it is in the recycle bin where the person can restore it.</summary>
        public async Task<bool> MediaIsSafeAsync(Scene scene)
        {
            if (File.Exists(scene.StagingPath))
            {
                return true;
            }

            return (await Bin.ListAsync(CancellationToken.None)).Count > 0;
        }

        public void Dispose() => _storage.Dispose();

        private static bool TryCreateHardlink(string existingPath, string newPath)
            => OperatingSystem.IsWindows()
                ? CreateHardLink(newPath, existingPath, IntPtr.Zero)
                : link(existingPath, newPath) == 0;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [DllImport("libc", SetLastError = true, EntryPoint = "link")]
        private static extern int link(string oldpath, string newpath);
    }
}
