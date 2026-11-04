using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>#1155, #1325: what the sweep removes from a processor output folder and why, and everything that keeps it from removing.</summary>
public sealed class ProcessorOutputSweepBranchesTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T03:00:00Z");

    private readonly string _root = Directory.CreateTempSubdirectory("deluno-sweep-").FullName;
    private readonly string _downloads;
    private readonly string _ready;

    public ProcessorOutputSweepBranchesTests()
    {
        _downloads = Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;
        _ready = Directory.CreateDirectory(Path.Combine(_root, "ready")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Put(string relative)
    {
        var path = Path.Combine(_ready, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        return path;
    }

    private LibraryItem Library(string id = "library-1", string name = "Movies", string workflow = "refine-before-import", string? output = "<ready>", string? root = "/library")
        => new(id, name, "movies", "main", root!, _downloads, null, null, null,
            true, true, workflow, "Weir", output == "<ready>" ? _ready : output, 360, "block",
            true, true, true, 6, 6, 10, null, null, "active", false, null, null, Now, Now);

    private static ProcessorHandoffItem Handoff(string id, string release, string status = "released", string client = "client-1", string? queueItem = "q1", DateTimeOffset? created = null)
        => new(id, "library-1", "movie", client, queueItem!, release, "/downloads/" + release, "Weir", status, null, null, null, created ?? Now, Now);

    private sealed class Rig
    {
        public Mock<ILibrariesRepository> Libraries { get; } = new();
        public Mock<IProcessorRepository> Processors { get; } = new();
        public Mock<IJobQueueRepository> Jobs { get; } = new();
        public Mock<IMovieCatalogRepository> Movies { get; } = new();
        public Mock<ISeriesCatalogRepository> Series { get; } = new();
        public Mock<IActivityFeedRepository> Activity { get; } = new();
        public Mock<IHealthCheckRepository> Health { get; } = new();
        public List<(string Message, string? Details, string? EntityId)> Said { get; } = [];
        public Func<string, IEnumerable<string>>? ListEntries;
        public Func<string, IEnumerable<string>>? ListFiles;
        public Func<string, FileAttributes>? Attributes;
        public Action<string>? DeleteFolder;
        public Action<string>? DeleteFile;

        public Rig(LibraryItem[] libraries, ProcessorHandoffItem[] handoffs, JobQueueItem[]? jobs = null, DownloadDispatchItem[]? dispatches = null)
        {
            Libraries.Setup(r => r.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(libraries);
            Processors.Setup(r => r.ListProcessorHandoffsAsync("library-1", It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(handoffs);
            Jobs.Setup(r => r.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(jobs ?? []);
            Jobs.Setup(r => r.ListDownloadDispatchesAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<string?>()))
                .ReturnsAsync(dispatches ?? []);
            Activity.Setup(a => a.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, string?, string?, string?, CancellationToken>((_, message, details, _, _, entityId, _) => Said.Add((message, details, entityId)))
                .ReturnsAsync((ActivityEventItem)null!);
        }

        public Task<int> SweepAsync()
            => new ProcessorOutputSweep(Libraries.Object, Processors.Object, Jobs.Object, Movies.Object, Series.Object, Activity.Object, Health.Object,
                ListEntries, ListFiles, Attributes, DeleteFolder, DeleteFile).SweepAsync(CancellationToken.None);
    }

    private static DownloadDispatchItem Dispatch(string release, DateTimeOffset created, string entity = "m1", string? importStatus = null, string client = "client-1")
        => new("d" + created.Ticks + release, "library-1", "movies", "movie", entity, release, "Indexer", client, "Client", "completed", null, created,
            null, null, null, null, null, null, null, null, null, importStatus, null, null, null, null, null, null);

    [Fact]
    public async Task A_released_hand_off_loses_its_release_folder_with_a_line_in_Activity_and_its_health_row()
    {
        Put("Heat.1995/Heat.1995.mkv");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995")]);

        var removed = await rig.SweepAsync();

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(Path.Combine(_ready, "Heat.1995")));
        var said = Assert.Single(rig.Said);
        Assert.Equal("Deleted the folder Heat.1995 from the Movies library's processor output folder: Deluno imported the original download instead of this processed copy.", said.Message);
        Assert.Equal("library-1", said.EntityId);
        Assert.Contains("\"Automatic\":true", said.Details);
        Assert.Contains("\"LibraryName\":\"Movies\"", said.Details);
        rig.Health.Verify(h => h.RemoveAsync("processor-output:library-1:heat.1995", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("processing.output.deleted", ProcessorOutputSweep.ActivityCategory);
    }

    [Fact]
    public async Task A_released_hand_off_loses_a_loose_file_and_the_line_names_the_file()
    {
        var file = Put("Heat.1995.mkv");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995.mkv")]);

        Assert.Equal(1, await rig.SweepAsync());
        Assert.False(File.Exists(file));
        Assert.StartsWith("Deleted Heat.1995.mkv from the Movies library's processor output folder:", Assert.Single(rig.Said).Message);
    }

    [Fact]
    public async Task Overlapping_library_folders_stop_all_clean_up()
    {
        Put("Heat.1995/Heat.1995.mkv");
        var rig = new Rig([Library(), Library("library-2", "Shows")], [Handoff("h1", "Heat.1995")]);

        Assert.Equal(0, await rig.SweepAsync());
        Assert.True(Directory.Exists(Path.Combine(_ready, "Heat.1995")));
    }

    [Theory]
    [InlineData("import-directly", "<ready>")]
    [InlineData("refine-before-import", null)]
    [InlineData("refine-before-import", "  ")]
    public async Task A_library_that_hands_nothing_to_a_processor_is_not_swept(string workflow, string? output)
    {
        Put("Heat.1995/Heat.1995.mkv");
        var rig = new Rig([Library(workflow: workflow, output: output)], [Handoff("h1", "Heat.1995")]);

        Assert.Equal(0, await rig.SweepAsync());
        rig.Jobs.Verify(j => j.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_output_folder_that_is_not_there_is_skipped()
    {
        var rig = new Rig([Library(output: Path.Combine(_root, "missing"))], [Handoff("h1", "Heat.1995")]);

        Assert.Equal(0, await rig.SweepAsync());
        rig.Processors.Verify(p => p.ListProcessorHandoffsAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    [InlineData("argument")]
    [InlineData("notsupported")]
    public async Task An_output_folder_that_cannot_be_listed_is_left_alone(string kind)
    {
        Put("Heat.1995/Heat.1995.mkv");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995")])
        {
            ListEntries = _ => throw (kind switch { "io" => new IOException(), "denied" => new UnauthorizedAccessException(), "argument" => new ArgumentException(), _ => (Exception)new NotSupportedException() })
        };

        Assert.Equal(0, await rig.SweepAsync());
    }

    [Fact]
    public async Task An_import_still_queued_for_a_file_keeps_it_and_one_dead_lettered_or_of_another_kind_does_not()
    {
        var file = Put("Heat.1995/Heat.1995.mkv");
        string Payload(string path) => "{\"preview\":{\"sourcePath\":\"" + path.Replace("\\", "\\\\") + "\"}}";
        var queued = TestJobs.Create("filesystem.import.execute", payloadJson: Payload(file), status: "queued");
        var dead = TestJobs.Create("filesystem.import.execute", payloadJson: Payload(file), status: "dead-letter");
        var other = TestJobs.Create("library.search.movies", payloadJson: Payload(file));
        var noSource = TestJobs.Create("filesystem.import.execute", payloadJson: "{}", status: "queued");

        Assert.Equal(0, await new Rig([Library()], [Handoff("h1", "Heat.1995")], [queued]).SweepAsync());
        Assert.True(File.Exists(file));
        Assert.Equal(1, await new Rig([Library()], [Handoff("h1", "Heat.1995")], [dead, other, noSource]).SweepAsync());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task A_file_still_being_written_keeps_its_folder()
    {
        var file = Put("Heat.1995/Heat.1995.mkv");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        Assert.Equal(0, await new Rig([Library()], [Handoff("h1", "Heat.1995")]).SweepAsync());
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    public async Task A_folder_whose_files_cannot_be_listed_is_not_taken_for_settled(string kind)
    {
        Put("Heat.1995/Heat.1995.mkv");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995")])
        {
            ListFiles = _ => throw (kind == "io" ? new IOException() : new UnauthorizedAccessException())
        };

        Assert.Equal(0, await rig.SweepAsync());
        Assert.True(Directory.Exists(Path.Combine(_ready, "Heat.1995")));
    }

    [Fact]
    public async Task An_item_no_hand_off_claims_is_left_to_the_safety_net()
    {
        var file = Put("Unrelated.Film.mkv");

        Assert.Equal(0, await new Rig([Library()], [Handoff("h1", "Heat.1995")]).SweepAsync());
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_hand_off_that_may_still_receive_output_keeps_the_item_even_when_an_older_one_was_released()
    {
        var file = Put("Heat.1995.mkv");
        var older = Handoff("h1", "Heat.1995", created: Now.AddDays(-1));
        var waiting = Handoff("h2", "Heat.1995", status: ProcessorHandoffStatuses.AcceptingLateOutput[0], created: Now);

        Assert.Equal(0, await new Rig([Library()], [older, waiting]).SweepAsync());
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task The_newest_of_the_hand_offs_that_claim_an_item_decides_why_it_goes()
    {
        var file = Put("Heat.1995.mkv");
        var older = Handoff("h1", "Heat.1995", status: "released", created: Now.AddDays(-1));
        var newer = Handoff("h2", "Heat.1995", status: ProcessorHandoffStatuses.Failed, created: Now);
        var rig = new Rig([Library()], [older, newer]);

        Assert.Equal(0, await rig.SweepAsync());
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_download_that_was_imported_makes_its_processed_copy_a_second_copy()
    {
        Put("Heat.1995.mkv");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995.mkv", status: ProcessorHandoffStatuses.Failed)], dispatches: [Dispatch("Heat.1995.mkv", Now, importStatus: "imported")]);

        Assert.Equal(1, await rig.SweepAsync());
        Assert.EndsWith("Deluno already imported this download, so this is a second copy.", Assert.Single(rig.Said).Message);
    }

    [Fact]
    public async Task A_newer_different_release_of_the_same_title_that_was_imported_replaces_an_older_one()
    {
        Put("Heat.1995.mkv");
        var older = Dispatch("Heat.1995.mkv", Now.AddDays(-2));
        var newer = Dispatch("Heat.1995.REPACK", Now, importStatus: "IMPORTED");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995.mkv", status: ProcessorHandoffStatuses.Failed)], dispatches: [newer, older]);

        Assert.Equal(1, await rig.SweepAsync());
        Assert.EndsWith("a newer download of the same title has since been imported, so this older one was replaced and Deluno will never import it.", Assert.Single(rig.Said).Message);
    }

    [Theory]
    [InlineData("other-client", "m1", "Heat.1995.REPACK", "imported", 1)]
    [InlineData("client-1", "m2", "Heat.1995.REPACK", "imported", 1)]
    [InlineData("client-1", "m1", "heat.1995", "imported", 1)]
    [InlineData("client-1", "m1", "Heat.1995.REPACK", "queued", 1)]
    [InlineData("client-1", "m1", "Heat.1995.REPACK", "imported", -1)]
    public async Task A_newer_download_replaces_only_when_it_is_the_same_title_a_different_release_imported_and_later(string client, string entity, string release, string import, int days)
    {
        Put("Heat.1995.mkv");
        var older = Dispatch("Heat.1995", Now);
        var other = Dispatch(release, Now.AddDays(days), entity, import, client);
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995", status: ProcessorHandoffStatuses.Failed)], dispatches: [other, older]);

        Assert.Equal(0, await rig.SweepAsync());
    }

    [Fact]
    public async Task A_title_taken_out_of_Deluno_since_the_grab_means_its_output_will_never_be_imported()
    {
        Put("Heat.1995.mkv");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995.mkv", status: ProcessorHandoffStatuses.Failed)]);
        rig.Jobs.Setup(j => j.FindDispatchLinkByClientItemAsync("client-1", "q1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("d1", "movie", "m1", LibraryId: "library-1"));
        rig.Movies.Setup(m => m.GetByIdAsync("m1", It.IsAny<CancellationToken>())).ReturnsAsync((MovieListItem?)null);

        Assert.Equal(1, await rig.SweepAsync());
        Assert.EndsWith(", so Deluno will never import it.", Assert.Single(rig.Said).Message.Replace("..", "."));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    public async Task A_delete_that_fails_removes_nothing_and_says_nothing(string kind)
    {
        Put("Heat.1995/Heat.1995.mkv");
        Put("Heat.1995.nfo");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995")])
        {
            DeleteFolder = _ => throw (kind == "io" ? new IOException() : new UnauthorizedAccessException()),
            DeleteFile = _ => throw (kind == "io" ? new IOException() : new UnauthorizedAccessException())
        };

        Assert.Equal(0, await rig.SweepAsync());
        Assert.Empty(rig.Said);
        rig.Health.Verify(h => h.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_item_that_vanished_before_it_could_be_deleted_counts_for_nothing()
    {
        Put("Heat.1995/Heat.1995.mkv");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995")])
        {
            ListFiles = folder =>
            {
                Directory.Delete(folder, recursive: true);
                return [];
            }
        };

        Assert.Equal(0, await rig.SweepAsync());
        Assert.Empty(rig.Said);
    }

    [Fact]
    public async Task A_library_folder_inside_the_item_is_never_removed()
    {
        Put("Heat.1995/Heat.1995.mkv");
        var inside = Library("library-2", "Shows", output: null, root: Path.Combine(_ready, "Heat.1995"));
        var rig = new Rig([Library(), inside], [Handoff("h1", "Heat.1995")]);

        Assert.Equal(0, await rig.SweepAsync());
        Assert.True(Directory.Exists(Path.Combine(_ready, "Heat.1995")));
    }

    [Fact]
    public async Task A_library_root_inside_its_own_output_item_is_never_removed()
    {
        Put("Heat.1995/Heat.1995.mkv");
        var rig = new Rig([Library(root: Path.Combine(_ready, "Heat.1995"))], [Handoff("h1", "Heat.1995")]);

        Assert.Equal(0, await rig.SweepAsync());
        Assert.True(Directory.Exists(Path.Combine(_ready, "Heat.1995")));
    }

    [Fact]
    public async Task With_no_download_of_its_own_on_record_a_failed_hand_off_gives_no_reason_to_remove()
    {
        var file = Put("Heat.1995.mkv");
        var other = Dispatch("Heat.1995.REPACK", Now.AddDays(1), importStatus: "imported");

        Assert.Equal(0, await new Rig([Library()], [Handoff("h1", "Heat.1995.mkv", status: ProcessorHandoffStatuses.Failed)], dispatches: [other]).SweepAsync());
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_newer_imported_download_of_another_kind_of_title_is_not_a_replacement()
    {
        var file = Put("Heat.1995.mkv");
        var own = Dispatch("Heat.1995.mkv", Now);
        var episode = Dispatch("Heat.1995.REPACK", Now.AddDays(1), importStatus: "imported") with { EntityType = "episode" };

        Assert.Equal(0, await new Rig([Library()], [Handoff("h1", "Heat.1995.mkv", status: ProcessorHandoffStatuses.Failed)], dispatches: [own, episode]).SweepAsync());
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_download_of_another_client_is_not_this_hand_offs_own()
    {
        var file = Put("Heat.1995.mkv");
        var foreign = Dispatch("Heat.1995.mkv", Now, client: "other-client");
        var newer = Dispatch("Heat.1995.REPACK", Now.AddDays(1), importStatus: "imported", client: "other-client");

        Assert.Equal(0, await new Rig([Library()], [Handoff("h1", "Heat.1995.mkv", status: ProcessorHandoffStatuses.Failed)], dispatches: [foreign, newer]).SweepAsync());
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_link_in_the_output_folder_is_never_followed_or_removed()
    {
        Put("Heat.1995/Heat.1995.mkv");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995")])
        {
            Attributes = _ => FileAttributes.Directory | FileAttributes.ReparsePoint
        };

        Assert.Equal(0, await rig.SweepAsync());
        Assert.True(Directory.Exists(Path.Combine(_ready, "Heat.1995")));
    }

    [Fact]
    public async Task An_item_whose_attributes_cannot_be_read_is_taken_for_a_link()
    {
        Put("Heat.1995/Heat.1995.mkv");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995")]) { Attributes = _ => throw new IOException() };
        var denied = new Rig([Library()], [Handoff("h1", "Heat.1995")]) { Attributes = _ => throw new UnauthorizedAccessException() };

        Assert.Equal(0, await rig.SweepAsync());
        Assert.Equal(0, await denied.SweepAsync());
    }

    [Fact]
    public async Task A_listing_that_gives_a_blank_name_or_a_path_that_is_not_a_direct_child_is_skipped()
    {
        Put("Heat.1995/Heat.1995.mkv");
        var rig = new Rig([Library()], [Handoff("h1", "Heat.1995")])
        {
            ListEntries = root => [root + Path.DirectorySeparatorChar, Path.Combine(root, "Heat.1995", "Heat.1995.mkv"), Path.Combine(_root, "downloads")]
        };

        Assert.Equal(0, await rig.SweepAsync());
        Assert.True(Directory.Exists(Path.Combine(_ready, "Heat.1995")));
    }
}
