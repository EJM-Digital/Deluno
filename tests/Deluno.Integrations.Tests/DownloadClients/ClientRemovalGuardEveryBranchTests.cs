using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Libraries.Contracts;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1268: the rest of the removal guard and of the folder removal a client's own delete leaves to Deluno: a client that cannot be
/// asked after the removal, a path that is not a path, a job's own folder inside a library, a single finished file, and a root.
/// </summary>
public sealed class ClientRemovalGuardEveryBranchTests
{
    private const string Job = "Some.Film.2020.1080p";

    private static LibraryItem Library(string root)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            "library-1", "Movies", "movies", "main", root, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deluno-1268-" + Guid.NewGuid().ToString("n"));

        public Scratch() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_client_that_cannot_be_asked_after_the_removal_is_not_said_to_have_removed_it()
    {
        var asks = 0;
        var result = await ClientRemovalGuard.RunAsync(
            Client("qbittorrent"), "abc", DownloadClientActions.Delete, "qBittorrent",
            has: () => Task.FromResult(++asks == 1 ? ClientHas.Yes : ClientHas.Unknown),
            remove: () => Task.FromResult(DeluNoSuccess()));

        Assert.False(result.Succeeded);
        Assert.Contains("did not confirm", result.Message);
    }

    private static DownloadClientActionResult DeluNoSuccess()
        => DownloadClientHelpers.ActionSuccess(Client("qbittorrent"), "abc", DownloadClientActions.Delete, "sent");

    [Fact]
    public void A_path_that_cannot_be_resolved_or_names_nothing_is_never_deleted()
    {
        Assert.False(DownloadFolderRemoval.IsSafeToDelete("/downloads/bad\0name", out _));
        Assert.False(DownloadFolderRemoval.IsSafeToDelete("  ", out _));
        Assert.False(DownloadFolderRemoval.IsSafeToDelete(null, out _));
        Assert.False(DownloadFolderRemoval.IsSafeToDelete(Path.GetPathRoot(Path.GetTempPath()), out _));
        Assert.False(DownloadFolderRemoval.IsSafeToDelete(Path.Combine(Path.GetTempPath(), "deluno-1268-missing-" + Guid.NewGuid().ToString("n")), out _));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(" ", false)]
    public void A_client_that_names_no_path_leaves_nothing_to_delete_here_and_nothing_to_look_for_elsewhere(string? path, bool onThisMachine)
    {
        var problem = DownloadFolderRemoval.Remove(path, Job, onThisMachine, []);

        Assert.Equal(onThisMachine ? null : "The client did not say where its files are, so Deluno could not look for them.", problem);
    }

    [Fact]
    public void A_path_that_is_not_there_is_gone_on_this_machine_and_unseen_on_another()
    {
        var missing = Path.Combine(Path.GetTempPath(), "deluno-1268-missing-" + Guid.NewGuid().ToString("n"), Job);

        Assert.Null(DownloadFolderRemoval.Remove(missing, Job, onThisMachine: true, []));
        Assert.Contains("another machine", DownloadFolderRemoval.Remove(missing, Job, onThisMachine: false, []));
    }

    [Fact]
    public void A_jobs_own_folder_inside_a_library_is_left_alone()
    {
        using var scratch = new Scratch();
        var library = Directory.CreateDirectory(Path.Combine(scratch.Path, "Media", "Movies")).FullName;
        var jobFolder = Directory.CreateDirectory(Path.Combine(library, Job)).FullName;
        var film = Path.Combine(jobFolder, Job + ".mkv");
        File.WriteAllText(film, "film");

        var problem = DownloadFolderRemoval.Remove(film, Job, onThisMachine: true, [Library(library)]);

        Assert.True(File.Exists(film));
        Assert.Contains("inside a library", problem);
    }

    [Fact]
    public void A_jobs_own_folder_that_holds_a_library_is_left_alone()
    {
        using var scratch = new Scratch();
        var jobFolder = Directory.CreateDirectory(Path.Combine(scratch.Path, "Completed", "Movies", Job)).FullName;
        var library = Directory.CreateDirectory(Path.Combine(jobFolder, "Library")).FullName;
        var film = Path.Combine(jobFolder, Job + ".mkv");
        File.WriteAllText(film, "film");

        var problem = DownloadFolderRemoval.Remove(film, Job, onThisMachine: true, [Library(library)]);

        Assert.True(File.Exists(film));
        Assert.Contains("holds one", problem);
    }

    [Fact]
    public void A_finished_file_named_after_the_job_in_a_shared_folder_goes_alone_and_a_stranger_stays()
    {
        using var scratch = new Scratch();
        var shared = Directory.CreateDirectory(Path.Combine(scratch.Path, "Completed", "Movies")).FullName;
        var film = Path.Combine(shared, Job + ".mkv");
        var stranger = Path.Combine(shared, "Another.Film.2019.mkv");
        File.WriteAllText(film, "film");
        File.WriteAllText(stranger, "other");

        var strangerProblem = DownloadFolderRemoval.Remove(stranger, Job, onThisMachine: true, []);
        var problem = DownloadFolderRemoval.Remove(film, Job, onThisMachine: true, []);

        Assert.True(File.Exists(stranger));
        Assert.Contains("not named after this download", strangerProblem);
        Assert.Null(problem);
        Assert.False(File.Exists(film));
    }

    [Fact]
    public void A_file_with_no_folder_above_it_is_not_a_jobs_own_folder()
    {
        Assert.Null(DownloadFolderRemoval.JobOwnFolder(Path.GetPathRoot(Path.GetTempPath())!, Job));
        Assert.Null(DownloadFolderRemoval.JobOwnFolder(Path.Combine(Path.GetTempPath(), "x.mkv"), "  "));
    }

    [Fact]
    public void Leftover_folders_are_removed_only_when_empty_and_only_when_the_library_allows()
    {
        using var scratch = new Scratch();
        var folder = Directory.CreateDirectory(Path.Combine(scratch.Path, "Downloads")).FullName;
        var empty = Directory.CreateDirectory(Path.Combine(folder, Job + "__abc123")).FullName;
        var holding = Directory.CreateDirectory(Path.Combine(folder, Job + "__def456")).FullName;
        File.WriteAllText(Path.Combine(holding, "part.bin"), "x");
        Directory.CreateDirectory(Path.Combine(folder, "Unrelated"));

        var refusal = DownloadFolderRemoval.RemoveEmptyLeftovers(folder, Job, null);
        var done = DownloadFolderRemoval.RemoveEmptyLeftovers(folder, Job, []);

        Assert.Contains("could not check", refusal);
        Assert.Null(done);
        Assert.False(Directory.Exists(empty));
        Assert.True(Directory.Exists(holding));
        Assert.Null(DownloadFolderRemoval.RemoveEmptyLeftovers(null, Job, []));
        Assert.Null(DownloadFolderRemoval.RemoveEmptyLeftovers(folder, " ", []));
    }
}
