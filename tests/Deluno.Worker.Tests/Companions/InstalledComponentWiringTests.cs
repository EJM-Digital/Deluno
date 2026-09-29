using Deluno.Api.Network;
using Deluno.Contracts;
using Deluno.Companions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Jobs.Data;
using Deluno.Worker.Companions;
using Moq;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// #1054: an app the picker installed becomes a Deluno connection with no clicks, once,
/// with the sign-in the installer left for it, tested, and reported in plain words.
/// </summary>
public sealed class InstalledComponentWiringTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-wiring-tests", Guid.NewGuid().ToString("N"));
    private readonly List<CreateDownloadClientRequest> _created = [];
    private readonly List<(string Id, string Status, string Message)> _health = [];
    private readonly List<string> _activity = [];
    private readonly Dictionary<string, BundledComponentCredentials> _saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileInstalledComponentRecord _record;
    private string _testStatus = "healthy";
    private string _testMessage = "Connected.";

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
    [InlineData("qbittorrent", "qBittorrent", 8081, "deluno-movies", "deluno-tv", "deluno", "Zx-secret")]
    [InlineData("transmission", "Transmission", 9091, "deluno-movies", "deluno-tv", null, null)]
    [InlineData("deluge", "Deluge", 8112, "deluno-movies", "deluno-tv", null, "web-pass")]
    [InlineData("sabnzbd", "SABnzbd", 8080, "Movies", "TV", null, "api-key-1")]
    [InlineData("nzbget", "NZBGet", 6789, "Movies", "TV", "deluno", "nzb-pass")]
    public async Task Each_installed_download_client_is_added_with_its_own_port_categories_and_sign_in(
        string kind, string name, int port, string movies, string tv, string? username, string? secret)
    {
        _record.MarkInstalled(kind, "localhost", port);
        if (secret is not null)
        {
            _saved[kind] = new BundledComponentCredentials(username ?? string.Empty, secret);
        }

        await NewWirer().RunOnceAsync("http://127.0.0.1:7879", 1, TimeSpan.Zero, CancellationToken.None);

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
        Assert.Contains(_activity, line => line.Contains($"Connected {name} to Deluno", StringComparison.Ordinal));
        Assert.NotNull(_record.Read().Single().WiredUtc);
    }

    [Fact]
    public async Task The_installers_plain_sign_in_is_forgotten_once_the_connection_tests_healthy()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "Zx-secret");

        await NewWirer().RunOnceAsync("http://127.0.0.1:7879", 1, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("Zx-secret", Assert.Single(_created).Password);
        Assert.False(_saved.ContainsKey("qbittorrent"));
    }

    [Fact]
    public async Task The_installers_plain_sign_in_is_kept_while_the_connection_does_not_test_healthy()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        _saved["qbittorrent"] = new BundledComponentCredentials("deluno", "qb-secret");
        _testStatus = "unreachable";
        _testMessage = "No answer.";

        await NewWirer().RunOnceAsync("http://127.0.0.1:7879", 1, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("qb-secret", _saved["qbittorrent"].Password);
        Assert.Equal("deluno", Assert.Single(_created).Username);
    }

    [Fact]
    public async Task SABnzbd_without_a_saved_key_is_connected_with_the_key_read_from_its_own_settings()
    {
        _record.MarkInstalled("sabnzbd", "localhost", 8080);
        var system = new Mock<ICompanionSystem>();
        system.SetupGet(item => item.LocalAppData).Returns(Path.Combine(_folder, "local"));
        system.SetupGet(item => item.RoamingAppData).Returns(Path.Combine(_folder, "roaming"));
        system.Setup(item => item.ReadText(Path.Combine(_folder, "local", "sabnzbd", "sabnzbd.ini"))).Returns("[misc]\napi_key = abc123\n");

        await NewWirer(system.Object).RunOnceAsync("http://127.0.0.1:7879", 1, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("abc123", Assert.Single(_created).Password);
    }

    [Fact]
    public async Task An_app_is_connected_once_and_a_connection_the_person_deletes_is_not_made_again()
    {
        _record.MarkInstalled("nzbget", "localhost", 6789);
        var wirer = NewWirer();

        await wirer.RunOnceAsync("http://127.0.0.1:7879", 1, TimeSpan.Zero, CancellationToken.None);
        // The person deletes the connection; the next pass finds nothing to do.
        var second = await wirer.RunOnceAsync("http://127.0.0.1:7879", 1, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(0, second);
        Assert.False(wirer.HasWork());
        Assert.Single(_created);
    }

    [Fact]
    public async Task Reinstalling_does_not_forget_that_an_app_was_already_connected()
    {
        _record.MarkInstalled("nzbget", "localhost", 6789);
        _record.MarkWired("nzbget");

        _record.MarkInstalled("nzbget", "localhost", 6789);

        Assert.NotNull(_record.Read().Single().WiredUtc);
        await NewWirer().RunOnceAsync("http://127.0.0.1:7879", 1, TimeSpan.Zero, CancellationToken.None);
        Assert.Empty(_created);
    }

    [Fact]
    public async Task A_failed_test_keeps_the_connection_and_says_in_plain_words_what_failed()
    {
        _record.MarkInstalled("deluge", "localhost", 8112);
        _testStatus = "unreachable";
        _testMessage = "Deluge did not answer on port 8112.";

        await NewWirer().RunOnceAsync("http://127.0.0.1:7879", 2, TimeSpan.Zero, CancellationToken.None);

        Assert.Single(_created);
        Assert.Contains(_health, item => item.Status == "unreachable" && item.Message == "Deluge did not answer on port 8112.");
        var line = Assert.Single(_activity);
        Assert.Contains("Deluge was installed and added to Deluno, but its connection test did not pass", line, StringComparison.Ordinal);
        Assert.Contains("did not answer on port 8112", line, StringComparison.Ordinal);
        // Tried once: a failure is reported, not retried forever, and the connection stays.
        Assert.NotNull(_record.Read().Single().WiredUtc);
    }

    [Fact]
    public async Task Weir_is_connected_through_the_processor_path_and_named_in_the_activity_line()
    {
        _record.MarkInstalled("weir", "localhost", 9347);
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        var weir = new Mock<ICompanionTool>();
        weir.SetupGet(item => item.Id).Returns(CompanionToolIds.Weir);
        weir.SetupGet(item => item.Name).Returns("Weir");
        weir.Setup(item => item.IsInstalled()).Returns(true);
        CompanionRunContext? seen = null;
        weir.Setup(item => item.RunAsync(It.IsAny<CompanionRunContext>(), It.IsAny<CancellationToken>()))
            .Callback<CompanionRunContext, CancellationToken>((context, _) => seen = context)
            .Returns(Task.CompletedTask);

        await NewWirer(weir: weir.Object).RunOnceAsync("http://127.0.0.1:7879", 1, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("http://127.0.0.1:7879", seen?.DelunoBaseUrl);
        var line = Assert.Single(_activity);
        Assert.Equal("Connected qBittorrent and Weir to Deluno after installing them. Each connection was tested and passed.", line);
        Assert.All(_record.Read(), entry => Assert.NotNull(entry.WiredUtc));
    }

    [Fact]
    public async Task A_Weir_test_that_fails_is_reported_with_its_reason()
    {
        _record.MarkInstalled("weir", "localhost", 9347);
        var weir = new Mock<ICompanionTool>();
        weir.SetupGet(item => item.Id).Returns(CompanionToolIds.Weir);
        weir.SetupGet(item => item.Name).Returns("Weir");
        weir.Setup(item => item.IsInstalled()).Returns(true);
        weir.Setup(item => item.RunAsync(It.IsAny<CompanionRunContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CompanionStepException("Weir is installed and added to Deluno, but its test did not pass: it refused the sign-in."));

        await NewWirer(weir: weir.Object).RunOnceAsync("http://127.0.0.1:7879", 1, TimeSpan.Zero, CancellationToken.None);

        Assert.Contains("it refused the sign-in", Assert.Single(_activity), StringComparison.Ordinal);
    }

    /// <summary>#1055: the summary line never says everything passed when something did not.</summary>
    [Fact]
    public async Task The_activity_names_what_passed_and_what_did_not_with_the_reason()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);
        _record.MarkInstalled("weir", "localhost", 9347);
        var weir = new Mock<ICompanionTool>();
        weir.SetupGet(item => item.Id).Returns(CompanionToolIds.Weir);
        weir.SetupGet(item => item.Name).Returns("Weir");
        weir.Setup(item => item.IsInstalled()).Returns(true);
        weir.Setup(item => item.RunAsync(It.IsAny<CompanionRunContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CompanionStepException("Weir is installed and added to Deluno, but its test did not pass: the processor answered its health check with HTTP 503."));

        await NewWirer(weir: weir.Object).RunOnceAsync("http://127.0.0.1:7879", 1, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(2, _activity.Count);
        Assert.Contains(_activity, line => line.Contains("HTTP 503", StringComparison.Ordinal));
        var summary = Assert.Single(_activity, line => line.StartsWith("Connected", StringComparison.Ordinal));
        Assert.Contains("qBittorrent", summary, StringComparison.Ordinal);
        Assert.Contains("Weir did not pass its test", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Each connection was tested and passed", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_error_leaves_the_app_for_the_next_pass()
    {
        _record.MarkInstalled("transmission", "localhost", 9091);
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database not ready"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewWirer(connections: connections.Object).RunOnceAsync("http://127.0.0.1:7879", 1, TimeSpan.Zero, CancellationToken.None));

        Assert.Null(_record.Read().Single().WiredUtc);
    }

    [Fact]
    public void The_picker_record_is_only_written_for_installed_apps_and_survives_a_restart()
    {
        _record.MarkInstalled("qbittorrent", "localhost", 8081);

        var again = new FileInstalledComponentRecord(Path.Combine(_folder, "installed-components.json"));

        Assert.Equal("qbittorrent", Assert.Single(again.Read()).Kind);
        Assert.Null(again.Read().Single().WiredUtc);
    }

    private InstalledComponentWirer NewWirer(ICompanionSystem? system = null, ICompanionTool? weir = null, IConnectionsRepository? connections = null)
    {
        connections ??= FakeConnections();
        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(repo => repo.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((_, message, _, _, _, _, _) => _activity.Add(message))
            .ReturnsAsync((Deluno.Jobs.Contracts.ActivityEventItem)null!);
        var credentials = new Mock<IBundledComponentCredentialStore>();
        credentials.Setup(store => store.TryGet(It.IsAny<string>())).Returns((string kind) => _saved.GetValueOrDefault(kind));
        credentials.Setup(store => store.Remove(It.IsAny<string>())).Callback((string kind) => _saved.Remove(kind));
        var tools = weir is null ? Array.Empty<ICompanionTool>() : [weir];
        return new InstalledComponentWirer(
            _record,
            credentials.Object,
            new DownloadClientConnector(connections, new FakeTester(this)),
            tools,
            system ?? Mock.Of<ICompanionSystem>(),
            activity.Object);
    }

    private IConnectionsRepository FakeConnections()
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
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
            => Task.FromResult<(string, string, string?)>((owner._testStatus, owner._testMessage, null));
    }
}
