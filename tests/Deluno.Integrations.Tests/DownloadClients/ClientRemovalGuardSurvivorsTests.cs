using Deluno.Integrations.DownloadClients;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Moq;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1269: the mutants of <c>ClientRemovalGuard</c> and <c>DownloadFolderRemoval</c> that the branch tests let through: the
/// exact settle timing (how many looks, how long between them), the words of each refusal, and the folder name rules.
/// </summary>
public sealed class ClientRemovalGuardSurvivorsTests
{
    private const string Job = "Some.Film.2020.1080p";

    private static DownloadClientActionResult Sent()
        => DownloadClientHelpers.ActionSuccess(Client("nzbget"), "abc", DownloadClientActions.Delete, "sent");

    private static DownloadClientActionResult Unreachable
        => DownloadClientHelpers.ActionFailure(Client("nzbget"), "abc", DownloadClientActions.Delete, "x", category: "unreachable");

    private static DownloadClientActionResult Rejected
        => DownloadClientHelpers.ActionFailure(Client("nzbget"), "abc", DownloadClientActions.Delete, "x");

    /// <summary>The pauses the guard asked for, in order. The guard is given a delay that records the pause and returns at once (#1506), so
    /// the tests count and add up pauses instead of timing a real wait, which a busy runner distorts.</summary>
    private sealed class Pauses
    {
        public List<TimeSpan> Asked { get; } = [];

        public Task Delay(TimeSpan pause, CancellationToken cancellationToken)
        {
            Asked.Add(pause);
            return Task.CompletedTask;
        }
    }

    private static Task<DownloadClientActionResult> Run(Func<Task<ClientHas>> has, TimeSpan window = default, TimeSpan poll = default, Pauses? pauses = null)
        => ClientRemovalGuard.RunAsync(Client("nzbget"), "abc", DownloadClientActions.Delete, "NZBGet", has, () => Task.FromResult(Sent()), window, poll,
            delay: (pauses ?? new Pauses()).Delay);

    /// <summary>A client that lists the download before the removal and keeps listing it for <paramref name="stillThere"/> looks.</summary>
    private static Func<Task<ClientHas>> StaysFor(int stillThere, List<int> looks)
    {
        var asked = 0;
        return () =>
        {
            looks.Add(asked);
            asked++;
            return Task.FromResult(asked <= 1 + stillThere ? ClientHas.Yes : ClientHas.No);
        };
    }

    [Fact]
    public async Task A_client_that_cannot_be_asked_first_says_so_and_changes_nothing()
    {
        var removed = false;
        var result = await ClientRemovalGuard.RunAsync(
            Client("nzbget"), "abc", DownloadClientActions.Delete, "NZBGet",
            () => Task.FromResult(ClientHas.Unknown), () => { removed = true; return Task.FromResult(Sent()); });

        Assert.False(removed);
        Assert.False(result.Succeeded);
        Assert.Equal("NZBGet could not be asked whether it has this download, so nothing was changed.", result.Message);
        Assert.Equal(Unreachable.Failure!.Kind, result.Failure!.Kind);
        Assert.NotEqual(Rejected.Failure!.Kind, result.Failure.Kind);
    }

    [Fact]
    public async Task A_client_that_has_no_such_download_says_it_was_never_removed()
    {
        var result = await Run(() => Task.FromResult(ClientHas.No));

        Assert.Equal("NZBGet has no download with that id, so nothing was removed. It may already have been removed there.", result.Message);
    }

    [Fact]
    public async Task A_client_that_cannot_be_asked_after_says_it_did_not_confirm()
    {
        var asks = 0;
        var result = await Run(() => Task.FromResult(++asks == 1 ? ClientHas.Yes : ClientHas.Unknown));

        Assert.Equal("NZBGet did not confirm that the download is gone, so Deluno cannot say it was removed.", result.Message);
        Assert.Equal(Unreachable.Failure!.Kind, result.Failure!.Kind);
        Assert.NotEqual(Rejected.Failure!.Kind, result.Failure.Kind);
    }

    [Fact]
    public async Task A_download_still_listed_with_no_window_is_a_failure_that_names_no_wait()
    {
        var result = await Run(() => Task.FromResult(ClientHas.Yes));

        Assert.False(result.Succeeded);
        Assert.Equal("NZBGet still has this download after being told to remove it, so it was not removed.", result.Message);
    }

    [Fact]
    public async Task The_looks_are_spaced_by_the_poll_that_doubles_and_never_run_past_the_window()
    {
        // Poll 100 ms, window 350 ms: pauses of 100, 200, then what is left, 50.
        var looks = new List<int>();
        var pauses = new Pauses();
        var result = await Run(StaysFor(3, looks), TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(100), pauses);

        // The first look is before the removal, the second straight after; then one per pause: 1 + 1 + 3 looks, the last one saying No.
        Assert.Equal(5, looks.Count);
        Assert.True(result.Succeeded);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50)], pauses.Asked);
    }

    [Fact]
    public async Task With_no_poll_given_the_first_pause_is_a_quarter_of_a_second()
    {
        var looks = new List<int>();
        var pauses = new Pauses();
        var result = await Run(StaysFor(1, looks), TimeSpan.FromMilliseconds(600), pauses: pauses);

        Assert.True(result.Succeeded);
        Assert.Equal(3, looks.Count);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], pauses.Asked);
    }

    [Fact]
    public async Task A_download_that_never_goes_is_waited_for_the_whole_window_and_the_wait_is_said_in_seconds()
    {
        // 1.4 s: pauses of 1 s and then the 0.4 s left. Waiting the poll's 2 s the second time would say 3 seconds.
        var pauses = new Pauses();
        var result = await Run(() => Task.FromResult(ClientHas.Yes), TimeSpan.FromMilliseconds(1400), TimeSpan.FromSeconds(1), pauses);

        Assert.False(result.Succeeded);
        Assert.Equal("NZBGet still has this download 1 seconds after being told to remove it, so it was not removed.", result.Message);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(400)], pauses.Asked);
    }

    [Fact]
    public async Task The_pause_grows_to_five_seconds_and_no_further_than_the_window()
    {
        // Poll 3 s doubles to 6 s, but five seconds is the longest pause: a 9 s window is 3 s + 5 s + 1 s, three looks after the removal.
        var looks = new List<int>();
        var pauses = new Pauses();
        var result = await ClientRemovalGuard.RunAsync(
            Client("nzbget"), "abc", DownloadClientActions.Delete, "NZBGet",
            () => { looks.Add(looks.Count); return Task.FromResult(ClientHas.Yes); },
            () => Task.FromResult(Sent()), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(3), delay: pauses.Delay);

        Assert.False(result.Succeeded);
        Assert.Equal(5, looks.Count);
        Assert.Contains("9 seconds", result.Message);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)], pauses.Asked);
    }

    [Fact]
    public async Task A_cancelled_wait_stops_at_once()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClientRemovalGuard.RunAsync(
            Client("nzbget"), "abc", DownloadClientActions.Delete, "NZBGet",
            () => Task.FromResult(ClientHas.Yes), () => Task.FromResult(Sent()),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), cancel.Token));
    }

    [Fact]
    public async Task Libraries_that_cannot_be_read_are_null_and_a_cancelled_read_is_not_swallowed()
    {
        var broken = new Mock<ILibrariesRepository>();
        broken.Setup(item => item.ListLibrariesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db"));
        Assert.Null(await DownloadFolderRemoval.ReadLibrariesAsync(broken.Object, CancellationToken.None));

        var cancelled = new Mock<ILibrariesRepository>();
        cancelled.Setup(item => item.ListLibrariesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => DownloadFolderRemoval.ReadLibrariesAsync(cancelled.Object, CancellationToken.None));

        Assert.Empty((await DownloadFolderRemoval.ReadLibrariesAsync(null, CancellationToken.None))!);
    }

    [Fact]
    public void A_path_that_is_not_safe_leaves_the_full_path_empty()
    {
        Assert.False(DownloadFolderRemoval.IsSafeToDelete("/downloads/../etc", out var climbing));
        Assert.Equal(string.Empty, climbing);
        Assert.False(DownloadFolderRemoval.IsSafeToDelete(null, out var none));
        Assert.Equal(string.Empty, none);
    }

    [Fact]
    public void A_file_in_a_folder_is_the_jobs_when_the_folder_is_named_after_it_and_a_folder_is_judged_by_its_own_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-1269-" + Guid.NewGuid().ToString("n"));
        try
        {
            var shared = Directory.CreateDirectory(Path.Combine(root, "Completed", "Movies")).FullName;
            // A folder that carries another name is left alone, and one that carries the job's name goes.
            var stranger = Directory.CreateDirectory(Path.Combine(shared, "Another.Film.2019")).FullName;
            var own = Directory.CreateDirectory(Path.Combine(shared, Job)).FullName;
            // A file, not a folder, is judged by its name without its extension.
            var film = Path.Combine(shared, Job + ".mkv");
            File.WriteAllText(film, "film");

            Assert.Contains("not named after", DownloadFolderRemoval.Remove(stranger, Job, true, []));
            Assert.True(Directory.Exists(stranger));
            Assert.Null(DownloadFolderRemoval.Remove(own, Job, true, []));
            Assert.False(Directory.Exists(own));
            // A job given no name has nothing to compare, so a folder is deleted as the client said.
            Assert.Null(DownloadFolderRemoval.Remove(stranger, "  ", true, []));
            Assert.False(Directory.Exists(stranger));
            // A folder named after only the start of a longer job name, and the other way round, is the job's.
            var longer = Directory.CreateDirectory(Path.Combine(shared, Job + ".GRP")).FullName;
            Assert.Null(DownloadFolderRemoval.Remove(longer, Job, true, []));
            var shorter = Directory.CreateDirectory(Path.Combine(shared, "Some.Film")).FullName;
            Assert.Null(DownloadFolderRemoval.Remove(shorter, Job, true, []));
            // A file named Job.mkv: its stem (not its whole name) is compared with the job.
            Assert.Null(DownloadFolderRemoval.Remove(film, Job, true, []));
            Assert.False(File.Exists(film));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_file_is_judged_by_its_stem_and_a_folder_by_its_whole_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-1269-" + Guid.NewGuid().ToString("n"));
        try
        {
            var shared = Directory.CreateDirectory(Path.Combine(root, "Completed", "Movies")).FullName;
            // A file: its stem "Another.Film" starts the job name, its whole name "Another.Film.mkv" does not.
            var film = Path.Combine(shared, "Another.Film.mkv");
            File.WriteAllText(film, "film");
            Assert.Null(DownloadFolderRemoval.Remove(film, "Another.Film.2019", true, []));
            Assert.False(File.Exists(film));
            // A folder whose name ends like an extension is not stripped: ".XYZ" is part of its name, and it is not the job's.
            var folder = Directory.CreateDirectory(Path.Combine(shared, "Some.Film.2020.1080p.XYZ")).FullName;
            Assert.Contains("not named after", DownloadFolderRemoval.Remove(folder, "Some.Film.2020.1080p.GRP", true, []));
            Assert.True(Directory.Exists(folder));
            // A job with no name at all compares nothing, so the client's folder goes.
            Assert.Null(DownloadFolderRemoval.Remove(folder, null, true, []));
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_folder_inside_a_folder_named_after_the_job_is_deleted_alone()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-1269-" + Guid.NewGuid().ToString("n"));
        try
        {
            var outer = Directory.CreateDirectory(Path.Combine(root, "Completed", "Movies", Job)).FullName;
            var inner = Directory.CreateDirectory(Path.Combine(outer, Job)).FullName;
            File.WriteAllText(Path.Combine(outer, "keep.nfo"), "x");

            Assert.Null(DownloadFolderRemoval.Remove(inner, Job, true, []));

            Assert.False(Directory.Exists(inner));
            Assert.True(File.Exists(Path.Combine(outer, "keep.nfo")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Makes <paramref name="folder"/> refuse changes: an open handle on Windows, no write permission elsewhere. False when that cannot be done (running as root).</summary>
    private static bool Refuse(string folder, string file, List<IDisposable> undo)
    {
        if (OperatingSystem.IsWindows())
        {
            undo.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None));
            return true;
        }

        return RefuseOnUnix(folder, undo);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static bool RefuseOnUnix(string folder, List<IDisposable> undo)
    {
        var before = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        undo.Add(new Undo(() => File.SetUnixFileMode(folder, before)));
        try
        {
            File.WriteAllText(Path.Combine(folder, ".probe"), "x");
            File.Delete(Path.Combine(folder, ".probe"));
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private sealed class Undo(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    [Fact]
    public void A_file_that_cannot_be_deleted_is_waited_for_then_given_up_on_in_words()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-1269-" + Guid.NewGuid().ToString("n"));
        var undo = new List<IDisposable>();
        try
        {
            var jobFolder = Directory.CreateDirectory(Path.Combine(root, "Completed", "Movies", Job)).FullName;
            var film = Path.Combine(jobFolder, Job + ".mkv");
            File.WriteAllText(film, "film");
            if (!Refuse(jobFolder, film, undo))
            {
                return;
            }

            var pauses = new List<TimeSpan>();
            var problem = DownloadFolderRemoval.Remove(film, Job, true, [], TimeSpan.FromMilliseconds(600), pauses.Add);

            Assert.StartsWith("Deluno could not delete ", problem);
            Assert.Contains(Job, problem);
            // Counted, not timed (#1506): it paused for the 600 ms it was given (three quarter-second pauses), not the default ten seconds.
            Assert.Equal(3, pauses.Count);
            Assert.All(pauses, pause => Assert.Equal(DownloadFolderRemoval.LockedFilePause, pause));
        }
        finally
        {
            foreach (var item in undo) item.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_leftover_folder_that_cannot_be_removed_is_named_in_the_problem()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // an empty folder cannot be held open on Windows from a test; the Linux run covers this
        }

        var root = Path.Combine(Path.GetTempPath(), "deluno-1269-" + Guid.NewGuid().ToString("n"));
        var undo = new List<IDisposable>();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName;
            var leftover = Directory.CreateDirectory(Path.Combine(folder, Job + "__abc123")).FullName;
            if (!Refuse(folder, Path.Combine(folder, "x"), undo))
            {
                return;
            }

            var problem = DownloadFolderRemoval.RemoveEmptyLeftovers(folder, Job, []);

            Assert.StartsWith("Deluno could not remove the empty folder ", problem);
            Assert.Contains(leftover, problem);
        }
        finally
        {
            foreach (var item in undo) item.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_download_folder_that_cannot_be_read_is_named_in_the_problem()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "deluno-1269-" + Guid.NewGuid().ToString("n"));
        var folder = Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName;
        var before = File.GetUnixFileMode(folder);
        try
        {
            File.SetUnixFileMode(folder, UnixFileMode.None);
            try
            {
                _ = Directory.EnumerateDirectories(folder).ToList();
                return; // running as root: nothing is unreadable
            }
            catch (UnauthorizedAccessException)
            {
            }

            var problem = DownloadFolderRemoval.RemoveEmptyLeftovers(folder, Job, []);

            Assert.StartsWith("Deluno could not look for leftover folders in ", problem);
            Assert.Contains(folder, problem);
        }
        finally
        {
            File.SetUnixFileMode(folder, before);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_file_whose_folder_is_a_shared_top_level_one_is_never_taken_as_the_jobs_folder()
    {
        var temp = Path.GetTempPath();
        var root = Path.Combine(temp, "deluno-1269-" + Guid.NewGuid().ToString("n"));
        // Parent named like the job, but sitting directly under the root: that is the completed area, not the job.
        Assert.Null(DownloadFolderRemoval.JobOwnFolder(Path.Combine(root, Job, "a.mkv"), Job + ".nope"));
        // A parent whose name only starts with the job's name still counts, one that does not start with it does not.
        Assert.Equal(Path.Combine(root, "Movies", Job + ".GRP"), DownloadFolderRemoval.JobOwnFolder(Path.Combine(root, "Movies", Job + ".GRP", "a.mkv"), Job));
        Assert.Null(DownloadFolderRemoval.JobOwnFolder(Path.Combine(root, "Movies", "Other", "a.mkv"), Job));
        // Only the file's own parent counts: a file whose parent is the root of the volume has none.
        Assert.Null(DownloadFolderRemoval.JobOwnFolder(Path.Combine(Path.GetPathRoot(temp)!, "a.mkv"), Job));
        // A file one level under a top-level folder: its parent is the completed area itself.
        Assert.Null(DownloadFolderRemoval.JobOwnFolder(Path.Combine(Path.GetPathRoot(temp)!, Job, "a.mkv"), Job));
    }

    [Fact]
    public void Leftover_folders_need_the_exact_name_shape_to_go()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-1269-" + Guid.NewGuid().ToString("n"));
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName;
            var good = Directory.CreateDirectory(Path.Combine(folder, Job + "__aB3dE9")).FullName;
            var tooShort = Directory.CreateDirectory(Path.Combine(folder, Job + "__abc12")).FullName;
            var tooLong = Directory.CreateDirectory(Path.Combine(folder, Job + "__abc1234")).FullName;
            var notLetters = Directory.CreateDirectory(Path.Combine(folder, Job + "__abc-12")).FullName;
            var otherName = Directory.CreateDirectory(Path.Combine(folder, "Other.Film__abc123")).FullName;
            // Empty sub folders do not count as files: the folder still goes. A real file anywhere inside keeps it.
            Directory.CreateDirectory(Path.Combine(good, "sub"));
            var deep = Directory.CreateDirectory(Path.Combine(folder, Job + "__zzzzzz")).FullName;
            Directory.CreateDirectory(Path.Combine(deep, "a", "b"));
            File.WriteAllText(Path.Combine(deep, "a", "b", "part"), "x");
            // The file's name does not count: only a file does, at any depth, whatever its name ("*").
            Assert.Null(DownloadFolderRemoval.RemoveEmptyLeftovers(folder, Job, []));

            Assert.False(Directory.Exists(good));
            Assert.True(Directory.Exists(tooShort));
            Assert.True(Directory.Exists(tooLong));
            Assert.True(Directory.Exists(notLetters));
            Assert.True(Directory.Exists(otherName));
            Assert.True(Directory.Exists(deep));
            // Nothing to do when the download folder is blank or the torrent has no name, or the folder is not there.
            Assert.Null(DownloadFolderRemoval.RemoveEmptyLeftovers(" ", Job, []));
            Assert.Null(DownloadFolderRemoval.RemoveEmptyLeftovers(folder, null, []));
            // A torrent with no name must not turn "__" and six letters into a name to clear.
            var bare = Directory.CreateDirectory(Path.Combine(folder, "__abc123")).FullName;
            Assert.Null(DownloadFolderRemoval.RemoveEmptyLeftovers(folder, " ", []));
            Assert.True(Directory.Exists(bare));
            Assert.Null(DownloadFolderRemoval.RemoveEmptyLeftovers(Path.Combine(root, "nope"), Job, []));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
