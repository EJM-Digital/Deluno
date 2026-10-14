using Deluno.Api.Network;
using Deluno.Contracts;
using Deluno.Companions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Jobs.Data;
using Deluno.Worker.Companions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// #1061, #968: the installer installs the ticked apps and Deluno connects nothing by itself. The
/// person connects them (one, or all) and Deluno answers whether it worked, never with a password.
/// </summary>
public sealed class InstalledComponentWiringTests : IDisposable
{
    private const string Url = "http://127.0.0.1:7879";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-wiring-tests", Guid.NewGuid().ToString("N"));
    private readonly List<CreateDownloadClientRequest> _created = [];
    private readonly List<(string Id, string Status, string Message)> _health = [];
    private readonly List<string> _activity = [];
    private readonly Dictionary<string, BundledComponentCredentials> _saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DownloadClientItem> _existing = [];
    private readonly FileInstalledComponentRecord _record;
    private string _testStatus = "healthy";
    private string _testMessage = "Connected.";
    private string? _testCategory;

    public InstalledComponentWiringTests()
    {
        _record = new FileInstalledComponentRecord(Path.Combine(_folder, "installed-components.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("qbittorrent", "qBittorrent", 8081, "deluno-movies", "deluno-tv", "deluno", "Zx-secret", true)]
    [InlineData("transmission", "Transmission", 9091, "Movies", "TV", null, null, false)]
    // Deluge and NZBGet are given no login by the installer, so Deluno connects with the one each ships with (#1061).
    [InlineData("deluge", "Deluge", 8112, "deluno-movies", "deluno-tv", null, "deluge", false)]
    // Deluge's web password is one the installer generated and kept; Deluno connects with that, not the shipped one.
    [InlineData("deluge", "Deluge", 8112, "deluno-movies", "deluno-tv", null, "Generated-pw-9", true)]
    [InlineData("sabnzbd", "SABnzbd", 8080, "Movies", "TV", null, "api-key-1", true)]
    [InlineData("nzbget", "NZBGet", 6789, "Movies", "TV", "nzbget", "tegbzn6789", false)]
    public async Task Each_installed_download_client_is_added_when_asked_with_its_own_port_categories_and_sign_in(
        string kind, string name, int port, string movies, string tv, string? username, string? secret, bool installerSavedIt)
    {
        _record.MarkInstalled(kind, "localhost", port);
        if (installerSavedIt)
        {
            _saved[kind] = new BundledComponentCredentials(username ?? string.Empty, secret!);
        }

        var result = await NewWirer().ConnectAsync(kind, null, null, Url, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(name, result.Name);
        Assert.True(result.Healthy);
        Assert.DoesNotContain(secret ?? "no-secret", result.Message, StringComparison.Ordinal);
        var request = Assert.Single(_created);
        Assert.Equal(name, request.Name);
        Assert.Equal(kind, request.Protocol);
        Assert.Equal("localhost", request.Host);
        Assert.Equal(port, request.Port);
        Assert.Equal(username, request.Username);
        Assert.Equal(secret, request.Password);
        Assert.Equal(movies, request.MoviesCategory);
        Assert.Equal(tv, request.TvCategory);
        Assert.True(request.IsEnabled);
        Assert.Contains(_health, item => item.Status == "healthy");
        Assert.Contains(_activity, line => line.Contains($"You connected {name} to Deluno", StringComparison.Ordinal));
        Assert.All(_activity, line => Assert.DoesNotContain(secret ?? "no-secret", line, StringComparison.Ordinal));
        Assert.NotNull(_record.Read().Single().WiredUtc);
    }

    // Golden path, rc.30.8: a new NZBGet starts paused, and a restart after the installer's own resume put that back.
    [Theory]
    [InlineData("nzbget", true, 1)]
    [InlineData("qbittorrent", true, 0)]
    [InlineData("nzbget", false, 0)]
    public async Task A_new_nzbget_is_resumed_when_it_is_first_connected_and_nothing_else_is_touched(string kind, bool isNew, int resumes)
    {
        _record.MarkInstalled(kind, "localhost", kind == "nzbget" ? 6789 : 8081);
        if (kind == "qbittorrent")
        {
            _saved[kind] = new BundledComponentCredentials("deluno", "qb-secret");
        }

        if (!isNew)
        {
            _existing.Add(new DownloadClientItem("old", "NZBGet", "nzbget", "localhost", 6789, "nzbget", "x", null, false, null, "Movies", "TV", null, 10, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        var resumed = 0;
        var implementation = new Mock<Deluno.Integrations.DownloadClients.IDownloadClient>();
        implementation.Setup(client => client.ResumeClientAsync(It.IsAny<DownloadClientItem>(), It.IsAny<CancellationToken>()))
            .Callback(() => resumed++)
            .ReturnsAsync(new Deluno.Integrations.DownloadClients.ClientResumeResult(true, true, "NZBGet is downloading again."));
        implementation.Setup(client => client.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadClientItem item, string category, CancellationToken _) => new Deluno.Integrations.DownloadClients.DownloadClientCategoryCheckResult(item.Id, item.Name, category, "ready", "ok", Supported: true, Found: true));
        var registry = new Mock<Deluno.Integrations.DownloadClients.IDownloadClientRegistry>();
        var handle = implementation.Object;
        registry.Setup(item => item.TryGet(It.IsAny<string>(), out handle)).Returns(true);

        var result = await NewWirer(registry: registry.Object).ConnectAsync(kind, null, null, Url, CancellationToken.None);

        Assert.True(result!.Healthy);
        Assert.Equal(resumes, resumed);
        Assert.Equal(resumes, _activity.Count(line => line.Contains("is not paused", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task No_connection_is_made_on_start_after_install()
    {
        // Every kind is installed and has its installer login; the background service runs its passes.
        foreach (var kind in new[] { "qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget", "weir" })
        {
            _record.MarkInstalled(kind, "localhost", 8081);
        }

        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "qb-secret");
        var cleanup = new Mock<ICompanionLoginCleanup>();
        var processors = new Mock<Deluno.Platform.Data.IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var services = new ServiceCollection();
        services.AddSingleton(cleanup.Object);
        services.AddSingleton(new WeirHealthAddressBackfill(processors.Object, Mock.Of<Deluno.Platform.Contracts.IProcessorConnectionService>(), Mock.Of<IActivityFeedRepository>()));
        services.AddSingleton(NewWirer());
        var provider = services.BuildServiceProvider();

        using var service = new InstalledComponentWiringService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InstalledComponentWiringService>.Instance)
        {
            StartDelay = TimeSpan.Zero,
            TickInterval = TimeSpan.FromMilliseconds(10),
        };
        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                cleanup.Verify(item => item.ForgetAllAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
                break;
            }
            catch (MockException)
            {
                await Task.Delay(20); // reason: poll interval inside a bounded wait (deadline above)
            }
        }

        await Task.Delay(100); // reason: shows the clean-up ran once and not again; absence needs the loop given real time
        await service.StopAsync(CancellationToken.None);

        cleanup.Verify(item => item.ForgetAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(_created);
        Assert.Empty(_health);
        Assert.Empty(_activity);
        Assert.All(_record.Read(), entry => Assert.Null(entry.WiredUtc));
    }

    [Fact]
    public async Task Connect_all_connects_every_installed_client_not_yet_connected_and_leaves_Weir_for_the_persons_account()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        _record.MarkInstalled("nzbget", "localhost", 6789);
        _record.MarkInstalled("transmission", "localhost", 9091);
        _record.MarkInstalled("weir", "localhost", 9347);
        _existing.Add(ExistingClient("transmission", "localhost") with { Port = 9091 });
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "qb-secret");
        var weir = new Mock<IWeirConnector>();
        weir.Setup(item => item.ConnectionNameAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        weir.Setup(item => item.AccountStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new WeirAccountState(true, true, false));

        var results = await NewWirer(weir: weir.Object).ConnectAllAsync(CancellationToken.None);

        Assert.Equal(["qbittorrent", "nzbget", "weir"], results.Select(item => item.Kind).ToArray());
        Assert.Equal(["qbittorrent", "nzbget"], _created.Select(item => item.Protocol!).ToArray());
        var weirResult = results.Single(item => item.Kind == "weir");
        Assert.False(weirResult.Healthy);
        Assert.True(weirResult.NeedsAccount);
        weir.Verify(item => item.ConnectAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Connect_all_says_nothing_about_Weir_when_it_is_not_installed_or_already_connected()
    {
        _record.MarkInstalled("nzbget", "localhost", 6789);
        var weir = new Mock<IWeirConnector>();
        weir.Setup(item => item.ConnectionNameAsync(It.IsAny<CancellationToken>())).ReturnsAsync("Weir");
        _record.MarkInstalled("weir", "localhost", 9347);

        var results = await NewWirer(weir: weir.Object).ConnectAllAsync(CancellationToken.None);

        Assert.Equal(["nzbget"], results.Select(item => item.Kind).ToArray());
    }

    [Fact]
    public async Task The_state_of_each_app_says_installed_connected_and_whether_Weir_needs_an_account()
    {
        _record.MarkInstalled("nzbget", "localhost", 6789);
        _record.MarkInstalled("transmission", "localhost", 9091);
        _record.MarkInstalled("weir", "localhost", 9347);
        _existing.Add(ExistingClient("transmission", "localhost") with { Port = null });
        var weir = new Mock<IWeirConnector>();
        weir.Setup(item => item.ConnectionNameAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        weir.Setup(item => item.AccountStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new WeirAccountState(true, true, false));
        var detected = BundledComponentCatalog.All
            .Select(spec => new BundledComponentStatus(spec.Kind, spec.DisplayName, spec.Category, false, false, "localhost", spec.DefaultPort))
            .ToArray();

        var states = await NewWirer(weir: weir.Object).StatesAsync(detected, CancellationToken.None);

        Assert.Equal(new InstalledComponentState(true, false, false, false), states["nzbget"]);
        Assert.Equal(new InstalledComponentState(false, false, false, false), states["deluge"]);
        Assert.Equal(new InstalledComponentState(true, true, false, false), states["transmission"]);
        Assert.Equal(new InstalledComponentState(true, false, true, false), states["weir"]);

        weir.Setup(item => item.AccountStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new WeirAccountState(true, false, true));
        var again = await NewWirer(weir: weir.Object).StatesAsync(detected, CancellationToken.None);
        Assert.Equal(new InstalledComponentState(true, false, false, true), again["weir"]);
    }

    [Fact]
    public async Task Connecting_Weir_hands_the_persons_account_to_Weirs_connector_and_keeps_it_nowhere()
    {
        const string PersonPassword = "My-Own-Weir-Pass-9";
        _record.MarkInstalled("weir", "localhost", 9347);
        var weir = new Mock<IWeirConnector>();
        weir.Setup(item => item.ConnectAsync("james", PersonPassword, Url, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeirConnectOutcome(true, "Weir is connected to Deluno both ways.", false));

        var result = await NewWirer(weir: weir.Object).ConnectAsync("weir", "james", PersonPassword, Url, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Healthy);
        Assert.Equal("weir", result.Kind);
        Assert.NotNull(_record.Read().Single().WiredUtc);
        Assert.All(_activity, line => Assert.DoesNotContain(PersonPassword, line, StringComparison.Ordinal));
        Assert.Contains(_activity, line => line.Contains("Weir", StringComparison.Ordinal));
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task Connecting_Weir_links_the_libraries_that_refine_before_import_and_leaves_the_others()
    {
        _record.MarkInstalled("weir", "localhost", 9347);
        var weir = new Mock<IWeirConnector>();
        weir.Setup(item => item.ConnectAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeirConnectOutcome(true, "Weir is connected.", false, "proc-1", "Weir"));
        var refining = Library("movies", ImportWorkflows.RefineBeforeImport, null, null);
        var refiningNamed = Library("tv", ImportWorkflows.RefineBeforeImport, null, "Weir");
        var standard = Library("docs", "standard", null, null);
        var alreadyLinked = Library("kids", ImportWorkflows.RefineBeforeImport, "proc-0", "Other");
        var otherName = Library("anime", ImportWorkflows.RefineBeforeImport, null, "FileFlows");
        var repository = new Mock<Deluno.Libraries.Data.ILibrariesRepository>();
        repository.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([refining, refiningNamed, standard, alreadyLinked, otherName]);
        var linked = new List<(string Id, Deluno.Libraries.Contracts.UpdateLibraryWorkflowRequest Request)>();
        repository.Setup(repo => repo.UpdateLibraryWorkflowAsync(It.IsAny<string>(), It.IsAny<Deluno.Libraries.Contracts.UpdateLibraryWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, Deluno.Libraries.Contracts.UpdateLibraryWorkflowRequest, CancellationToken>((id, request, _) => linked.Add((id, request)))
            .ReturnsAsync((string id, Deluno.Libraries.Contracts.UpdateLibraryWorkflowRequest _, CancellationToken _) => refining with { Id = id });

        var result = await NewWirer(weir: weir.Object, libraries: repository.Object).ConnectAsync("weir", "james", "pw", Url, CancellationToken.None);

        Assert.True(result!.Healthy);
        Assert.Equal(["movies", "tv"], linked.Select(item => item.Id).ToArray());
        Assert.All(linked, item =>
        {
            Assert.Equal("proc-1", item.Request.ProcessorConnectionId);
            Assert.Equal("Weir", item.Request.ProcessorName);
            Assert.Equal(ImportWorkflows.RefineBeforeImport, item.Request.ImportWorkflow);
        });
        Assert.Contains(_activity, line => line.Contains("Linked the movies library to Weir", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Weir_that_did_not_connect_links_no_library()
    {
        _record.MarkInstalled("weir", "localhost", 9347);
        var weir = new Mock<IWeirConnector>();
        weir.Setup(item => item.ConnectAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeirConnectOutcome(false, "Weir did not accept that.", false));
        var repository = new Mock<Deluno.Libraries.Data.ILibrariesRepository>(MockBehavior.Strict);

        var result = await NewWirer(weir: weir.Object, libraries: repository.Object).ConnectAsync("weir", "james", "bad", Url, CancellationToken.None);

        Assert.False(result!.Healthy);
        repository.VerifyNoOtherCalls();
    }

    private static Deluno.Libraries.Contracts.LibraryItem Library(string id, string importWorkflow, string? processorConnectionId, string? processorName)
        => new(
            Id: id, Name: id, MediaType: "movies", Purpose: "General", RootPath: "/media/" + id, DownloadsPath: null,
            QualityProfileId: null, QualityProfileName: null, CutoffQuality: null, UpgradeUntilCutoff: true, UpgradeUnknownItems: true,
            ImportWorkflow: importWorkflow, ProcessorName: processorName, ProcessorOutputPath: "/ready/" + id, ProcessorTimeoutMinutes: 60,
            ProcessorFailureMode: "hold", AutoSearchEnabled: false, MissingSearchEnabled: false, UpgradeSearchEnabled: false,
            SearchIntervalMinutes: 6, RetryDelayHours: 12, MaxItemsPerRun: 50, SearchWindowStartHour: null, SearchWindowEndHour: null,
            AutomationStatus: "active", SearchRequested: false, LastSearchedUtc: null, NextSearchUtc: null,
            CreatedUtc: DateTimeOffset.UnixEpoch, UpdatedUtc: DateTimeOffset.UnixEpoch)
        { ProcessorConnectionId = processorConnectionId };

    [Fact]
    public async Task A_Weir_that_did_not_accept_the_account_is_not_marked_connected()
    {
        _record.MarkInstalled("weir", "localhost", 9347);
        var weir = new Mock<IWeirConnector>();
        weir.Setup(item => item.ConnectAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeirConnectOutcome(false, "Weir did not accept that username and password.", false));

        var result = await NewWirer(weir: weir.Object).ConnectAsync("weir", "james", "wrong", Url, CancellationToken.None);

        Assert.False(result!.Healthy);
        Assert.Equal("Weir did not accept that username and password.", result.Message);
        Assert.Null(_record.Read().Single().WiredUtc);
        Assert.Empty(_activity);
    }

    [Fact]
    public async Task The_installers_sign_in_is_kept_while_the_app_is_installed_and_never_returned()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "qb-secret");

        var result = await NewWirer().ConnectAsync("qbittorrent", null, null, Url, CancellationToken.None);

        Assert.True(result!.Healthy);
        Assert.DoesNotContain("qb-secret", result.Message + result.Name, StringComparison.Ordinal);
        Assert.Equal("qb-secret", Assert.Single(_created).Password);
        Assert.True(_saved.ContainsKey("qbittorrent"));
    }

    [Fact]
    public async Task After_the_connection_is_deleted_Connect_works_again_with_the_kept_sign_in()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "qb-secret");
        var wirer = NewWirer();

        await wirer.ConnectAsync("qbittorrent", null, null, Url, CancellationToken.None);
        await wirer.DownloadClientDeletedAsync("qbittorrent", "localhost", CancellationToken.None);
        await wirer.SweepStoredSignInsAsync(CancellationToken.None);
        var again = await wirer.ConnectAsync("qbittorrent", null, null, Url, CancellationToken.None);

        Assert.True(again!.Healthy);
        Assert.Equal(2, _created.Count);
        Assert.All(_created, request => Assert.Equal("qb-secret", request.Password));
    }

    [Fact]
    public async Task A_persons_own_qBittorrent_on_another_port_does_not_stand_in_for_the_installers()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "qb-secret");
        _existing.Add(ExistingClient("qbittorrent", "localhost"));

        await NewWirer().ConnectAsync("qbittorrent", null, null, Url, CancellationToken.None);

        Assert.Equal(8081, Assert.Single(_created).Port);
        Assert.True(_saved.ContainsKey("qbittorrent"));
    }

    [Fact]
    public async Task Connecting_is_refused_for_a_kind_Deluno_cannot_connect_this_way()
    {
        Assert.Null(await NewWirer().ConnectAsync("nonsense", null, null, Url, CancellationToken.None));
    }

    [Fact]
    public async Task Connecting_Weir_off_Windows_says_so()
    {
        var result = await NewWirer().ConnectAsync("weir", "a", "b", Url, CancellationToken.None);

        Assert.False(result!.Healthy);
    }

    [Fact]
    public async Task Deleting_a_connection_keeps_the_stored_sign_in()
    {
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "qb-secret");

        await NewWirer().DownloadClientDeletedAsync("qbittorrent", "localhost", CancellationToken.None);

        Assert.True(_saved.ContainsKey("qbittorrent"));
    }

    [Fact]
    public async Task On_start_a_stored_sign_in_stays_for_an_installed_app_and_goes_when_the_app_is_gone()
    {
        // Kept: recorded as installed, connection or not.
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "keep-me");
        // Kept: no record (it failed to write) but the app is found installed.
        _saved["sabnzbd"] = new BundledComponentCredentials("", "key");
        // Dropped: no record, the app is uninstalled and not answering.
        _saved["nzbget"] = new BundledComponentCredentials("a", "b");
        // Dropped: not an app Deluno connects.
        _saved["mystery"] = new BundledComponentCredentials("d", "e");
        var detection = new Mock<IBundledComponentDetection>();
        detection.Setup(item => item.DetectAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new BundledComponentStatus("qbittorrent", "qBittorrent", "download-client", true, true, "localhost", 8081),
            new BundledComponentStatus("sabnzbd", "SABnzbd", "download-client", true, false, "localhost", 8080),
            new BundledComponentStatus("nzbget", "NZBGet", "download-client", false, false, "localhost", 6789)
        ]);

        var removed = await NewWirer(detection: detection.Object).SweepStoredSignInsAsync(CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.Equal(["qbittorrent", "sabnzbd"], _saved.Keys.OrderBy(key => key).ToArray());
    }

    // ---- #1260: an unread list is not an empty one; the sweep forgets sign-ins and that cannot be undone.

    [Fact]
    public async Task A_busy_install_record_does_not_make_the_sweep_forget_every_stored_sign_in()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "keep-me");
        var io = new Deluno.Tests.Support.ScriptedStateFileSystem { ReadFault = (_, _) => Deluno.Tests.Support.ScriptedStateFileSystem.SharingViolation() };
        var busy = new FileInstalledComponentRecord(Path.Combine(_folder, "installed-components.json"), io: io);

        var removed = await NewWirer(record: busy).SweepStoredSignInsAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.Equal(["qbittorrent"], _saved.Keys.ToArray());
    }

    [Fact]
    public async Task A_cut_short_install_record_does_not_make_the_sweep_forget_every_stored_sign_in()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "installed-components.json");
        File.WriteAllText(path, "[{\"Kind\":\"qbitt");
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "keep-me");

        var removed = await NewWirer(record: new FileInstalledComponentRecord(path)).SweepStoredSignInsAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.Equal(["qbittorrent"], _saved.Keys.ToArray());
    }

    [Theory]
    [InlineData(StateFileKind.Busy)]
    [InlineData(StateFileKind.Unreadable)]
    public async Task Stored_sign_ins_that_cannot_be_read_just_now_are_not_swept(StateFileKind availability)
    {
        _saved["mystery"] = new BundledComponentCredentials("d", "e");

        var removed = await NewWirer(credentialsAvailability: availability).SweepStoredSignInsAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.Contains("mystery", _saved.Keys);
    }

    [Fact]
    public async Task A_connection_that_works_is_still_reported_when_the_installed_list_is_busy_and_cannot_note_it()
    {
        _record.MarkInstalled("nzbget", "localhost", 6789);
        var io = new Deluno.Tests.Support.ScriptedStateFileSystem { ReadFault = (_, _) => Deluno.Tests.Support.ScriptedStateFileSystem.SharingViolation() };
        var busy = new FileInstalledComponentRecord(Path.Combine(_folder, "installed-components.json"), io: io);

        var result = await NewWirer(record: busy).ConnectAsync("nzbget", null, null, Url, CancellationToken.None);

        Assert.True(result!.Healthy);
        Assert.Contains(_activity, line => line.Contains("could not note that", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_way_to_look_the_install_record_alone_decides_what_a_stored_sign_in_is_kept_for()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "keep-me");
        _saved["deluge"] = new BundledComponentCredentials("", "c");

        Assert.Equal(1, await NewWirer().SweepStoredSignInsAsync(CancellationToken.None));
        Assert.Equal(["qbittorrent"], _saved.Keys.ToArray());
    }

    [Fact]
    public async Task A_detection_miss_never_overrides_the_install_record()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "keep-me");
        var detection = new Mock<IBundledComponentDetection>();
        detection.Setup(item => item.DetectAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            [new BundledComponentStatus("qbittorrent", "qBittorrent", "download-client", false, false, "localhost", 8081)]);
        var missed = new HashSet<string>();
        var wirer = NewWirer(detection: detection.Object);

        Assert.Equal(0, await wirer.SweepStoredSignInsAsync(CancellationToken.None, missed));
        Assert.Equal(0, await wirer.SweepStoredSignInsAsync(CancellationToken.None, missed));
        Assert.True(_saved.ContainsKey("qbittorrent"));
    }

    [Fact]
    public async Task An_app_with_no_record_that_looks_gone_is_forgotten_only_on_the_second_sweep_in_a_row()
    {
        _saved["nzbget"] = new BundledComponentCredentials("a", "b");
        var installed = false;
        var detection = new Mock<IBundledComponentDetection>();
        detection.Setup(item => item.DetectAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
            [new BundledComponentStatus("nzbget", "NZBGet", "download-client", installed, false, "localhost", 6789)]);
        var missed = new HashSet<string>();
        var wirer = NewWirer(detection: detection.Object);

        Assert.Equal(0, await wirer.SweepStoredSignInsAsync(CancellationToken.None, missed));
        Assert.True(_saved.ContainsKey("nzbget"));
        // Seen again: the miss is forgotten, so a later single miss does not count as the second.
        installed = true;
        Assert.Equal(0, await wirer.SweepStoredSignInsAsync(CancellationToken.None, missed));
        installed = false;
        Assert.Equal(0, await wirer.SweepStoredSignInsAsync(CancellationToken.None, missed));
        Assert.True(_saved.ContainsKey("nzbget"));
        Assert.Equal(1, await wirer.SweepStoredSignInsAsync(CancellationToken.None, missed));
        Assert.False(_saved.ContainsKey("nzbget"));
    }

    [Fact]
    public async Task Use_never_saves_a_connection_whose_test_failed_and_asks_for_the_persons_own_login()
    {
        _testStatus = "unauthorized";
        _testMessage = "NZBGet refused the sign-in.";

        var result = await NewWirer().ConnectAsync("nzbget", null, null, Url, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Healthy);
        Assert.True(result.NeedsSignIn);
        Assert.Equal("nzbget", result.Protocol);
        Assert.Equal(6789, result.Port);
        Assert.Contains("Nothing was saved", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tegbzn6789", result.Message, StringComparison.Ordinal);
        Assert.Empty(_created);
        Assert.Empty(_health);
    }

    [Fact]
    public async Task Use_saves_a_usenet_client_that_signs_in_but_has_no_provider_and_says_what_is_missing()
    {
        _testStatus = "degraded";
        _testMessage = Deluno.Connections.Usenet.UsenetProviderText.NoProvider;
        _testCategory = "configuration";

        var result = await NewWirer().ConnectAsync("nzbget", null, null, Url, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Healthy, "it is connected; the missing provider is said in the message");
        Assert.False(result.NeedsSignIn);
        Assert.Contains("No Usenet provider set up", result.Message, StringComparison.Ordinal);
        Assert.Single(_created);
        Assert.Contains(_health, item => item.Status == "degraded");
    }

    [Fact]
    public async Task A_saved_client_with_no_port_is_the_same_client_as_one_on_that_kinds_default_port()
    {
        var connections = FakeConnections();
        var saved = ExistingClient("nzbget", "localhost") with { Port = null };
        Mock.Get(connections).Setup(repo => repo.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([saved]);

        var result = await NewWirer(connections: connections).ConnectAsync("nzbget", null, null, Url, CancellationToken.None);

        Assert.True(result!.Healthy);
        Assert.Empty(_created);
    }

    private static DownloadClientItem ExistingClient(string protocol, string host)
        => new("existing-" + protocol, protocol, protocol, host, 8080, null, null, null,
            false, null, "m", "t", null, 10, true, "healthy", null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task SABnzbd_without_a_saved_key_is_connected_with_the_key_read_from_its_own_settings()
    {
        _record.MarkInstalled("sabnzbd", "localhost", 8080);
        var system = new Mock<ICompanionSystem>();
        system.SetupGet(item => item.LocalAppData).Returns(Path.Combine(_folder, "local"));
        system.SetupGet(item => item.RoamingAppData).Returns(Path.Combine(_folder, "roaming"));
        system.Setup(item => item.ReadText(Path.Combine(_folder, "local", "sabnzbd", "sabnzbd.ini"))).Returns("[misc]\napi_key = abc123\n");

        await NewWirer(system.Object).ConnectAsync("sabnzbd", null, null, Url, CancellationToken.None);

        Assert.Equal("abc123", Assert.Single(_created).Password);
    }


    [Fact]
    public void The_picker_record_is_only_written_for_installed_apps_and_survives_a_restart()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);

        var again = new FileInstalledComponentRecord(Path.Combine(_folder, "installed-components.json"));

        Assert.Equal("qbittorrent", Assert.Single(again.Read()).Kind);
        Assert.Null(again.Read().Single().WiredUtc);
    }

    private InstalledComponentWirer NewWirer(ICompanionSystem? system = null, IWeirConnector? weir = null, IConnectionsRepository? connections = null, IBundledComponentDetection? detection = null, Deluno.Libraries.Data.ILibrariesRepository? libraries = null, Deluno.Integrations.DownloadClients.IDownloadClientRegistry? registry = null, IInstalledComponentRecord? record = null, StateFileKind credentialsAvailability = StateFileKind.Ok)
    {
        connections ??= FakeConnections();
        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(repo => repo.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((_, message, _, _, _, _, _) => _activity.Add(message))
            .ReturnsAsync((Deluno.Jobs.Contracts.ActivityEventItem)null!);
        var credentials = new Mock<IBundledComponentCredentialStore>();
        credentials.Setup(store => store.TryGet(It.IsAny<string>())).Returns((string kind) => _saved.GetValueOrDefault(kind));
        credentials.Setup(store => store.Remove(It.IsAny<string>())).Callback((string kind) => _saved.Remove(kind));
        credentials.Setup(store => store.Has(It.IsAny<string>())).Returns((string kind) => _saved.ContainsKey(kind));
        credentials.Setup(store => store.Kinds()).Returns(() => _saved.Keys.ToArray());
        credentials.Setup(store => store.Availability()).Returns(credentialsAvailability);
        return new InstalledComponentWirer(
            record ?? _record,
            credentials.Object,
            new DownloadClientConnector(connections, new FakeTester(this)),
            connections,
            weir,
            system ?? Mock.Of<ICompanionSystem>(),
            activity.Object,
            registry: registry,
            detection: detection,
            libraries: libraries);
    }

    private IConnectionsRepository FakeConnections()
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _existing.ToList());
        connections.Setup(repo => repo.CreateDownloadClientAsync(It.IsAny<CreateDownloadClientRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateDownloadClientRequest, CancellationToken>((request, _) => _created.Add(request))
            .ReturnsAsync((CreateDownloadClientRequest request, CancellationToken _) => new DownloadClientItem(
                "client-" + _created.Count, request.Name!, request.Protocol!, request.Host, request.Port, request.Username, request.Password, null,
                false, null, request.MoviesCategory, request.TvCategory, null, 10, true, "unknown", null, null, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        connections.Setup(repo => repo.UpdateDownloadClientHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>(), It.IsAny<IntegrationFailure?>()))
            .Callback<string, string, string, string?, int?, CancellationToken, IntegrationFailure?>((id, status, message, _, _, _, _) => _health.Add((id, status, message)))
            .ReturnsAsync((IndexerTestResult?)null);
        return connections.Object;
    }

    private sealed class FakeTester(InstalledComponentWiringTests owner) : IDownloadClientTester
    {
        public Task<(string HealthStatus, string Message, string? FailureCategory)> TestAsync(DownloadClientItem client, CancellationToken cancellationToken)
            => Task.FromResult<(string, string, string?)>((owner._testStatus, owner._testMessage, owner._testCategory));
    }
}
