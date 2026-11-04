using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #868, #1060: every branch of the remote path mapping check, read against a pretend file system so the same
/// answers come on every machine: what a mapping's local folder is, what a client says it saves to, and how
/// that is translated through the mappings.
/// </summary>
public sealed class RemotePathMappingHealthCheckBranchesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private sealed class Folders
    {
        public HashSet<string> Existing { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Exception> Refusing { get; } = new(StringComparer.Ordinal);

        public bool Exists(string path) => Existing.Contains(path);

        public IEnumerable<string> List(string path)
        {
            // Like a real folder listing, it fails when the first entry is asked for, not when the listing is made.
            if (Refusing.TryGetValue(path, out var failure))
            {
                throw failure;
            }

            yield break;
        }
    }

    private sealed class Registry(IDownloadClient? client, string protocol = "qbittorrent") : IDownloadClientRegistry
    {
        public IReadOnlyCollection<string> KnownProtocols => [protocol];

        public bool TryGet(string? name, out IDownloadClient found)
        {
            found = client!;
            return client is not null && string.Equals(name, protocol, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static DownloadClientItem Client(string id = "client-1", string name = "Test Client", string? host = "NAS ", string? movies = "movies", string? tv = null, bool enabled = true, string protocol = "qbittorrent")
        => new(id, name, protocol, host, 8080, null, null, null, false, null, movies, tv, null, 1, enabled, "healthy", null, null, null, Now, Now, Now);

    private static DownloadClientPathMappingItem Mapping(string remote, string local, string clientId = "client-1", bool enabled = true, int priority = 1, string? host = null, string id = "mapping-1")
        => new(id, clientId, remote, local, enabled, priority, Now, Now, host);

    private static Mock<IDownloadClient> Implementation(Func<DownloadClientItem, string, Task<DownloadClientCategoryCheckResult>> check, bool perGrab = false)
    {
        var mock = new Mock<IDownloadClient>();
        mock.Setup(c => c.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((DownloadClientItem item, string category, CancellationToken _) => check(item, category));
        mock.SetupGet(c => c.SetsSaveFolderPerGrab).Returns(perGrab);
        return mock;
    }

    private static Task<DownloadClientCategoryCheckResult> Result(string category, string? savePath, string status = DownloadClientCategoryStatuses.Ready)
        => Task.FromResult(new DownloadClientCategoryCheckResult("client-1", "Test Client", category, status, "msg", true, true, savePath));

    private static RemotePathMappingHealthCheck Check(
        DownloadClientItem[] clients,
        DownloadClientPathMappingItem[] mappings,
        Folders folders,
        IDownloadClient? implementation = null)
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(c => c.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(clients);
        connections.Setup(c => c.ListDownloadClientPathMappingsAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(mappings);
        return new RemotePathMappingHealthCheck(connections.Object, new Registry(implementation), folders.Exists, folders.List);
    }

    [Fact]
    public async Task With_nothing_configured_it_says_it_is_not_in_use()
    {
        var check = Check([], [], new Folders());

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal("system:remote-path-mappings", outcome.Id);
        Assert.Equal("Remote path mappings", outcome.Name);
        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("Not in use: no download client path mappings are configured.", outcome.Message);
        Assert.Equal(HealthCheckCategories.RemotePathMapping, check.Category);
    }

    [Fact]
    public async Task A_mapping_whose_local_side_is_not_a_full_path_says_what_to_type()
    {
        var mapping = Mapping("/downloads", "relative/dir");

        var outcome = Assert.Single(await Check([Client()], [mapping], new Folders()).RunAsync(CancellationToken.None), item => item.Id == "remote-path-mapping:mapping-1");

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Test Client's path mapping (/downloads -> relative/dir)", outcome.Name);
        Assert.Equal("relative/dir is not a full path.", outcome.Message);
        Assert.Equal("Test Client's path mapping from /downloads needs a local side that is a full path Deluno can open directly.", outcome.Cause);
        Assert.Equal("Edit Test Client's mapping for /downloads and give it a full local path, such as C:\\Downloads\\Complete or \\\\nas\\downloads.", outcome.Fix);
    }

    [Fact]
    public async Task A_mapping_for_a_client_that_is_gone_names_a_download_client()
    {
        var mapping = Mapping("/downloads", "relative/dir", clientId: "gone");

        var outcome = Assert.Single(await Check([Client()], [mapping], new Folders()).RunAsync(CancellationToken.None), item => item.Id == "remote-path-mapping:mapping-1");

        Assert.StartsWith("a download client's path mapping", outcome.Name);
    }

    [Fact]
    public async Task A_mapping_is_matched_to_its_client_whatever_the_case_of_the_id()
    {
        var mapping = Mapping("/downloads", "relative/dir", clientId: "CLIENT-1");

        var outcome = Assert.Single(await Check([Client()], [mapping], new Folders()).RunAsync(CancellationToken.None), item => item.Id == "remote-path-mapping:mapping-1");

        Assert.StartsWith("Test Client's path mapping", outcome.Name);
    }

    [Fact]
    public async Task A_mapping_that_is_switched_off_is_not_checked()
    {
        var outcome = Assert.Single(await Check([], [Mapping("/downloads", "/mnt/dl", enabled: false)], new Folders()).RunAsync(CancellationToken.None));

        Assert.Equal("system:remote-path-mappings", outcome.Id);
    }

    [Fact]
    public async Task A_mapping_whose_local_folder_is_there_and_readable_is_ok()
    {
        var folders = new Folders();
        folders.Existing.Add("/mnt/dl");

        var outcome = Assert.Single(await Check([Client()], [Mapping("/downloads", "/mnt/dl")], folders).RunAsync(CancellationToken.None), item => item.Id == "remote-path-mapping:mapping-1");

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("/mnt/dl exists and Deluno can read it.", outcome.Message);
    }

    [Fact]
    public async Task A_mapping_whose_local_folder_is_not_there_says_to_reconnect_or_correct_it()
    {
        var outcome = Assert.Single(await Check([Client()], [Mapping("/downloads", "/mnt/dl")], new Folders()).RunAsync(CancellationToken.None), item => item.Id == "remote-path-mapping:mapping-1");

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("/mnt/dl does not exist.", outcome.Message);
        Assert.Equal("This mapping translates Test Client's /downloads to /mnt/dl, but that folder is not there -- a drive or share may be unmounted, or the path may no longer be correct.", outcome.Cause);
        Assert.Equal("Reconnect the drive or share, or correct the local path on Test Client's mapping for /downloads.", outcome.Fix);
    }

    public static TheoryData<string> Refusals => new() { "unauthorized", "io" };

    private static Exception Refusal(string kind) => kind == "io" ? new IOException("share is down") : new UnauthorizedAccessException("denied");

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_mapping_whose_local_folder_refuses_to_be_listed_says_it_is_a_permissions_problem(string kind)
    {
        var folders = new Folders();
        folders.Existing.Add("/mnt/dl");
        folders.Refusing["/mnt/dl"] = Refusal(kind);

        var outcome = Assert.Single(await Check([Client()], [Mapping("/downloads", "/mnt/dl")], folders).RunAsync(CancellationToken.None), item => item.Id == "remote-path-mapping:mapping-1");

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("/mnt/dl exists but Deluno cannot read it.", outcome.Message);
        Assert.Equal("/mnt/dl answers but refuses to be listed -- often a permissions problem.", outcome.Cause);
        Assert.Equal("Fix permissions on /mnt/dl so Deluno can read it for Test Client.", outcome.Fix);
    }

    [Fact]
    public async Task A_folder_that_fails_for_any_other_reason_is_not_hidden()
    {
        var folders = new Folders();
        folders.Existing.Add("/mnt/dl");
        folders.Refusing["/mnt/dl"] = new InvalidOperationException("a bug");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Check([Client()], [Mapping("/downloads", "/mnt/dl")], folders).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_client_with_an_unknown_protocol_or_that_is_switched_off_adds_nothing()
    {
        var implementation = Implementation((_, category) => Result(category, "/downloads"));

        var unknown = Check([Client(protocol: "mystery")], [], new Folders(), implementation.Object);
        var off = Check([Client(enabled: false)], [], new Folders(), implementation.Object);

        Assert.Equal("system:remote-path-mappings", Assert.Single(await unknown.RunAsync(CancellationToken.None)).Id);
        Assert.Equal("system:remote-path-mappings", Assert.Single(await off.RunAsync(CancellationToken.None)).Id);
        implementation.Verify(c => c.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_client_given_its_folder_on_every_grab_has_no_category_folder_to_check()
    {
        var implementation = Implementation((_, category) => Result(category, "/downloads"), perGrab: true);

        var outcome = Assert.Single(await Check([Client()], [], new Folders(), implementation.Object).RunAsync(CancellationToken.None));

        Assert.Equal("system:remote-path-mappings", outcome.Id);
    }

    [Fact]
    public async Task A_save_path_the_client_reports_that_is_there_is_ok_in_the_clients_words()
    {
        var folders = new Folders();
        folders.Existing.Add("/downloads/movies");
        var implementation = Implementation((_, category) => Result(category, "/downloads/movies"));

        var outcome = Assert.Single(await Check([Client()], [], folders, implementation.Object).RunAsync(CancellationToken.None));

        Assert.Equal("remote-path-mapping:save-path:client-1:movies", outcome.Id);
        Assert.Equal("Test Client's save path for movies", outcome.Name);
        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("Test Client saves movies to /downloads/movies, and Deluno can read it.", outcome.Message);
    }

    [Fact]
    public async Task A_save_path_that_is_not_there_says_to_reconnect_or_map_it()
    {
        var implementation = Implementation((_, category) => Result(category, "/downloads/movies"));

        var outcome = Assert.Single(await Check([Client()], [], new Folders(), implementation.Object).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Test Client saves movies to /downloads/movies, but that folder does not exist.", outcome.Message);
        Assert.Equal("Test Client's reported movies save path (/downloads/movies) translates to a local folder Deluno cannot find -- a drive or share may be unmounted, or a path mapping may need adding or correcting.", outcome.Cause);
        Assert.Equal("Reconnect the drive or share, or add/fix a remote path mapping for Test Client.", outcome.Fix);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_save_path_that_refuses_to_be_listed_says_it_is_a_permissions_problem(string kind)
    {
        var folders = new Folders();
        folders.Existing.Add("/downloads/movies");
        folders.Refusing["/downloads/movies"] = Refusal(kind);
        var implementation = Implementation((_, category) => Result(category, "/downloads/movies"));

        var outcome = Assert.Single(await Check([Client()], [], folders, implementation.Object).RunAsync(CancellationToken.None));

        Assert.Equal("Test Client saves movies to /downloads/movies, but Deluno cannot read it.", outcome.Message);
        Assert.Equal("/downloads/movies answers but refuses to be listed -- often a permissions problem.", outcome.Cause);
        Assert.Equal("Fix permissions on /downloads/movies so Deluno can read Test Client's movies downloads.", outcome.Fix);
    }

    [Fact]
    public async Task A_save_path_that_is_not_a_full_path_after_translation_says_to_add_a_mapping()
    {
        var implementation = Implementation((_, category) => Result(category, "movies-folder"));

        var outcome = Assert.Single(await Check([Client()], [], new Folders(), implementation.Object).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Equal("Test Client reports saving movies to movies-folder, which Deluno cannot open directly from here. Add a remote path mapping for this client so Deluno can translate it.", outcome.Message);
    }

    [Theory]
    [InlineData(DownloadClientCategoryStatuses.Unreachable, "/downloads/movies")]
    [InlineData(DownloadClientCategoryStatuses.Ready, null)]
    [InlineData(DownloadClientCategoryStatuses.Ready, "")]
    [InlineData(DownloadClientCategoryStatuses.Ready, "   ")]
    public async Task A_client_that_cannot_answer_or_does_not_say_where_it_saves_adds_nothing(string status, string? savePath)
    {
        var implementation = Implementation((_, category) => Result(category, savePath, status));

        var outcome = Assert.Single(await Check([Client()], [], new Folders(), implementation.Object).RunAsync(CancellationToken.None));

        Assert.Equal("system:remote-path-mappings", outcome.Id);
    }

    public static TheoryData<Exception> AskingFailures => new()
    {
        new HttpRequestException("connection refused"),
        new TaskCanceledException("timed out"),
        new InvalidOperationException("no secret"),
        new IOException("pipe broke")
    };

    [Theory]
    [MemberData(nameof(AskingFailures))]
    public async Task A_client_that_cannot_be_asked_is_unknown_with_the_reason(Exception failure)
    {
        var implementation = Implementation((_, _) => Task.FromException<DownloadClientCategoryCheckResult>(failure));

        var outcome = Assert.Single(await Check([Client()], [], new Folders(), implementation.Object).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Equal($"Deluno could not ask Test Client where it saves movies: {failure.Message}", outcome.Message);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_failure_to_ask_is_not_hidden()
    {
        var implementation = Implementation((_, _) => Task.FromException<DownloadClientCategoryCheckResult>(new ArgumentException("a bug")));

        await Assert.ThrowsAsync<ArgumentException>(() => Check([Client()], [], new Folders(), implementation.Object).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Each_distinct_category_is_asked_once_trimmed_and_blank_ones_are_skipped()
    {
        var asked = new List<string>();
        var implementation = Implementation((_, category) => { asked.Add(category); return Result(category, null); });

        await Check([Client(movies: " Movies ", tv: "movies")], [], new Folders(), implementation.Object).RunAsync(CancellationToken.None);
        await Check([Client(movies: "  ", tv: "tv")], [], new Folders(), implementation.Object).RunAsync(CancellationToken.None);
        await Check([Client(movies: null, tv: "")], [], new Folders(), implementation.Object).RunAsync(CancellationToken.None);

        Assert.Equal(["Movies", "tv"], asked);
    }

    [Fact]
    public async Task Both_categories_get_their_own_row()
    {
        var folders = new Folders();
        folders.Existing.Add("/downloads/movies");
        folders.Existing.Add("/downloads/tv");
        var implementation = Implementation((_, category) => Result(category, "/downloads/" + category));

        var outcomes = await Check([Client(movies: "movies", tv: "tv")], [], folders, implementation.Object).RunAsync(CancellationToken.None);

        Assert.Equal(["remote-path-mapping:save-path:client-1:movies", "remote-path-mapping:save-path:client-1:tv"], outcomes.Select(item => item.Id));
        Assert.All(outcomes, item => Assert.Equal(HealthCheckStatuses.Ok, item.Status));
    }

    [Fact]
    public async Task A_save_path_is_translated_through_its_clients_mapping_and_the_mapped_folder_is_read()
    {
        var folders = new Folders();
        folders.Existing.Add("/mnt/dl");
        var implementation = Implementation((_, category) => Result(category, "/downloads/"));

        var outcomes = await Check([Client()], [Mapping("/downloads", "/mnt/dl")], folders, implementation.Object).RunAsync(CancellationToken.None);

        var save = Assert.Single(outcomes, item => item.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
        Assert.Equal("Test Client saves movies to /mnt/dl, and Deluno can read it.", save.Message);
    }

    [Fact]
    public async Task A_path_below_the_mapped_folder_keeps_its_folders_and_the_slashes_are_not_mixed_up()
    {
        var expected = Path.Combine(Path.Combine("/mnt/dl", "Movies"), "Kids");
        var folders = new Folders();
        folders.Existing.Add(expected);
        var implementation = Implementation((_, category) => Result(category, "\\downloads\\Movies\\Kids\\"));

        var outcomes = await Check([Client()], [Mapping("/Downloads/", "/mnt/dl")], folders, implementation.Object).RunAsync(CancellationToken.None);

        var save = Assert.Single(outcomes, item => item.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
        Assert.Equal($"Test Client saves movies to {expected}, and Deluno can read it.", save.Message);
    }

    [Fact]
    public async Task A_folder_that_only_starts_with_the_same_letters_is_not_inside_the_mapping()
    {
        var implementation = Implementation((_, category) => Result(category, "/downloads-old/movies"));

        var outcomes = await Check([Client()], [Mapping("/downloads", "/mnt/dl")], new Folders(), implementation.Object).RunAsync(CancellationToken.None);

        var save = Assert.Single(outcomes, item => item.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
        Assert.Equal("Test Client saves movies to /downloads-old/movies, but that folder does not exist.", save.Message);
    }

    [Fact]
    public async Task The_longest_matching_mapping_wins_and_a_tie_goes_to_the_lower_priority_number()
    {
        var folders = new Folders();
        folders.Existing.Add("/mnt/specific");
        folders.Existing.Add("/mnt/first");
        var implementation = Implementation((_, category) => Result(category, category == "movies" ? "/data/media/movies" : "/data/tv"));
        var mappings = new[]
        {
            Mapping("/data", "/mnt/general", id: "m-general"),
            Mapping("/data/media/movies", "/mnt/specific", id: "m-specific"),
            Mapping("/data/tv", "/mnt/second", id: "m-second", priority: 5),
            Mapping("/data/tv", "/mnt/first", id: "m-first", priority: 2)
        };

        var outcomes = await Check([Client(movies: "movies", tv: "tv")], mappings, folders, implementation.Object).RunAsync(CancellationToken.None);

        var movies = Assert.Single(outcomes, item => item.Id.EndsWith(":movies", StringComparison.Ordinal) && item.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
        var tv = Assert.Single(outcomes, item => item.Id.EndsWith(":tv", StringComparison.Ordinal) && item.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
        Assert.Equal("Test Client saves movies to /mnt/specific, and Deluno can read it.", movies.Message);
        Assert.Equal("Test Client saves tv to /mnt/first, and Deluno can read it.", tv.Message);
    }

    [Fact]
    public async Task A_mapping_made_for_another_client_applies_only_when_it_is_at_the_same_host()
    {
        var folders = new Folders();
        folders.Existing.Add("/mnt/shared");
        var implementation = Implementation((_, category) => Result(category, "/downloads"));
        var sameHost = Mapping("/downloads", "/mnt/shared", clientId: "other-client", host: "nas");
        var otherHost = Mapping("/downloads", "/mnt/wrong", clientId: "other-client", host: "elsewhere");
        var noHost = Mapping("/downloads", "/mnt/wrong", clientId: "other-client", host: null);

        var shared = Check([Client()], [sameHost], folders, implementation.Object);
        var notShared = Check([Client()], [otherHost, noHost], folders, implementation.Object);

        var applied = Assert.Single(await shared.RunAsync(CancellationToken.None), item => item.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
        Assert.Equal("Test Client saves movies to /mnt/shared, and Deluno can read it.", applied.Message);
        var ignored = Assert.Single(await notShared.RunAsync(CancellationToken.None), item => item.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
        Assert.Equal("Test Client saves movies to /downloads, but that folder does not exist.", ignored.Message);
    }

    [Fact]
    public async Task A_client_with_no_host_matches_only_the_mappings_made_for_it()
    {
        var folders = new Folders();
        folders.Existing.Add("/mnt/own");
        var implementation = Implementation((_, category) => Result(category, "/downloads"));
        var own = Mapping("/downloads", "/mnt/own", clientId: "client-1");
        var foreign = Mapping("/downloads", "/mnt/wrong", clientId: "other-client", host: "nas", id: "m-foreign");

        var outcomes = await Check([Client(host: "  ")], [own, foreign], folders, implementation.Object).RunAsync(CancellationToken.None);

        var save = Assert.Single(outcomes, item => item.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
        Assert.Equal("Test Client saves movies to /mnt/own, and Deluno can read it.", save.Message);
    }

    [Fact]
    public async Task A_mapping_with_an_empty_host_is_not_shared_with_a_client_that_has_no_host()
    {
        var implementation = Implementation((_, category) => Result(category, "/downloads"));
        var emptyHost = Mapping("/downloads", "/mnt/wrong", clientId: "other-client", host: "");

        var outcomes = await Check([Client(host: " ")], [emptyHost], new Folders(), implementation.Object).RunAsync(CancellationToken.None);

        var save = Assert.Single(outcomes, item => item.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
        Assert.Equal("Test Client saves movies to /downloads, but that folder does not exist.", save.Message);
    }

    [Fact]
    public async Task A_save_path_equal_to_the_mapped_remote_folder_maps_to_the_local_folder_itself()
    {
        var folders = new Folders();
        folders.Existing.Add("/mnt/dl");
        var implementation = Implementation((_, category) => Result(category, "/downloads"));

        var outcomes = await Check([Client()], [Mapping("/downloads", "/mnt/dl")], folders, implementation.Object).RunAsync(CancellationToken.None);

        Assert.Contains(outcomes, item => item.Message == "Test Client saves movies to /mnt/dl, and Deluno can read it.");
    }

    [Fact]
    public async Task The_same_mapping_row_and_save_path_row_are_both_given_when_both_apply()
    {
        var folders = new Folders();
        folders.Existing.Add("/mnt/dl");
        var implementation = Implementation((_, category) => Result(category, "/downloads"));

        var outcomes = await Check([Client()], [Mapping("/downloads", "/mnt/dl")], folders, implementation.Object).RunAsync(CancellationToken.None);

        Assert.Equal(["remote-path-mapping:mapping-1", "remote-path-mapping:save-path:client-1:movies"], outcomes.Select(item => item.Id));
    }
}
