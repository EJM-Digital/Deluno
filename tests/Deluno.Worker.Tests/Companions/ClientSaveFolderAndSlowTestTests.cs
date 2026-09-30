using Deluno.Api.Network;
using Deluno.Companions;
using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Worker.Companions;
using Moq;

namespace Deluno.Worker.Tests.Companions;

/// <summary>#1060: category save folders exist after a clean install, and a slow first test is retried once.</summary>
public sealed class ClientSaveFolderAndSlowTestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-savefolders-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _activity = [];
    private readonly Mock<Deluno.Contracts.IHealthCheckRepository> _health = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_category_folders_are_created_inside_the_clients_own_download_folder_and_named_in_Activity()
    {
        var download = Path.Combine(_root, "complete");
        Directory.CreateDirectory(download);
        var existingFile = Path.Combine(download, "keep.txt");
        File.WriteAllText(existingFile, "x");

        await Wire("nzbget", 6789, "Movies", "TV", category => Path.Combine(download, category));

        Assert.True(Directory.Exists(Path.Combine(download, "Movies")));
        Assert.True(Directory.Exists(Path.Combine(download, "TV")));
        Assert.True(File.Exists(existingFile));
        Assert.Equal(3, Directory.GetFileSystemEntries(download).Length);
        Assert.Contains(_activity, line => line.StartsWith("Created the folders", StringComparison.Ordinal) && line.Contains("NZBGet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_client_folder_that_is_not_the_category_inside_a_download_folder_is_never_created()
    {
        var elsewhere = Path.Combine(_root, "somewhere", "custom-folder");

        await Wire("nzbget", 6789, "Movies", "TV", _ => elsewhere);

        Assert.False(Directory.Exists(elsewhere));
        Assert.False(Directory.Exists(Path.Combine(_root, "somewhere")));
        Assert.DoesNotContain(_activity, line => line.StartsWith("Created", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_client_given_a_folder_on_every_grab_gets_no_category_folders_made()
    {
        var download = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(download);

        await Wire("transmission", 9091, "deluno-movies", "deluno-tv", category => Path.Combine(download, category), perGrab: true);

        Assert.False(Directory.Exists(Path.Combine(download, "deluno-movies")));
        Assert.False(Directory.Exists(Path.Combine(download, "deluno-tv")));
        Assert.DoesNotContain(_activity, line => line.StartsWith("Created", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connecting_an_app_asks_Health_to_look_again_so_a_row_worked_out_mid_install_does_not_linger()
    {
        await Wire("nzbget", 6789, "Movies", "TV", category => Path.Combine(_root, "complete", category));

        _health.Verify(repo => repo.RequestImmediateRunAsync(Deluno.Contracts.HealthCheckCategories.DownloadClient, It.IsAny<CancellationToken>()), Times.Once);
        _health.Verify(repo => repo.RequestImmediateRunAsync(Deluno.Contracts.HealthCheckCategories.RemotePathMapping, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("C:\\", "Movies")]
    [InlineData("/Movies", "Movies")]
    [InlineData("C:\\Downloads\\..\\Movies", "Movies")]
    [InlineData("C:\\Downloads\\Movies", "..")]
    [InlineData("\\\\nas\\share\\Movies", "Movies")]
    [InlineData("C:\\Downloads\\Other", "Movies")]
    public void Unsafe_or_mismatched_paths_are_refused(string path, string category)
        => Assert.Null(InstalledComponentWirer.SafeCategoryFolderToCreate(path, category));

    [Fact]
    public void A_category_folder_directly_inside_a_download_folder_is_allowed()
        => Assert.Equal("C:\\Users\\a\\Downloads\\deluno-tv", InstalledComponentWirer.SafeCategoryFolderToCreate("C:\\Users\\a\\Downloads\\deluno-tv\\", "deluno-tv"));

    [Fact]
    public async Task A_timeout_is_retried_once_and_a_slow_answer_is_healthy_with_a_plain_note()
    {
        var budgets = new List<TimeSpan>();

        var result = await ConnectionsEndpointRouteBuilderExtensions.RunWithSlowRetryAsync(
            timeout =>
            {
                budgets.Add(timeout);
                return budgets.Count == 1
                    ? throw new TaskCanceledException("timed out", new TimeoutException())
                    : Task.FromResult<(string, string, string?)>(("healthy", "Connected to NZBGet at localhost:6789.", null));
            },
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal("healthy", result.healthStatus);
        Assert.Contains("slow to answer", result.message);
        Assert.Equal([TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20)], budgets);
    }

    [Fact]
    public async Task Two_timeouts_are_unreachable_after_exactly_two_tries()
    {
        var tries = 0;

        var result = await ConnectionsEndpointRouteBuilderExtensions.RunWithSlowRetryAsync(
            _ => { tries++; throw new TaskCanceledException("timed out", new TimeoutException()); },
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal(2, tries);
        Assert.Equal("unreachable", result.healthStatus);
    }

    [Fact]
    public async Task A_refusal_or_wrong_login_is_never_retried()
    {
        var tries = 0;
        var refused = await ConnectionsEndpointRouteBuilderExtensions.RunWithSlowRetryAsync(
            _ => { tries++; throw new HttpRequestException("Connection refused"); },
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20), CancellationToken.None);
        var auth = await ConnectionsEndpointRouteBuilderExtensions.RunWithSlowRetryAsync(
            _ => { tries++; return Task.FromResult<(string, string, string?)>(("degraded", "rejected authentication", "auth")); },
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal(2, tries);
        Assert.Equal("unreachable", refused.healthStatus);
        Assert.Equal("auth", auth.failureCategory);
    }

    [Fact]
    public async Task A_person_cancelling_is_not_treated_as_a_timeout()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var tries = 0;

        var result = await ConnectionsEndpointRouteBuilderExtensions.RunWithSlowRetryAsync(
            _ => { tries++; throw new TaskCanceledException("cancelled"); },
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20), cancel.Token);

        Assert.Equal(1, tries);
        Assert.Equal("unreachable", result.healthStatus);
    }

    private async Task Wire(string kind, int port, string movies, string tv, Func<string, string> folderFor, bool perGrab = false)
    {
        var record = new FileInstalledComponentRecord(Path.Combine(_root, "installed-components.json"));
        record.MarkInstalled(kind, "localhost", port);

        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connections.Setup(repo => repo.CreateDownloadClientAsync(It.IsAny<CreateDownloadClientRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateDownloadClientRequest request, CancellationToken _) => new DownloadClientItem(
                "client-1", request.Name!, request.Protocol!, request.Host, request.Port, request.Username, request.Password, null,
                false, null, request.MoviesCategory, request.TvCategory, null, 10, true, "unknown", null, null, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var implementation = new Mock<IDownloadClient>();
        implementation.Setup(client => client.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadClientItem item, string category, CancellationToken _) => new DownloadClientCategoryCheckResult(
                item.Id, item.Name, category, "ready", "ok", Supported: true, Found: true, SavePath: folderFor(category)));
        implementation.SetupGet(client => client.SetsSaveFolderPerGrab).Returns(perGrab);
        var registry = new Mock<IDownloadClientRegistry>();
        var handle = implementation.Object;
        registry.Setup(item => item.TryGet(kind, out handle)).Returns(true);

        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(repo => repo.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((_, message, _, _, _, _, _) => _activity.Add(message))
            .ReturnsAsync((Deluno.Jobs.Contracts.ActivityEventItem)null!);
        var credentials = new Mock<IBundledComponentCredentialStore>();

        var wirer = new InstalledComponentWirer(
            record, credentials.Object, new DownloadClientConnector(connections.Object, new HealthyTester()),
            connections.Object, null, Mock.Of<ICompanionSystem>(), activity.Object, registry.Object, null, _health.Object);
        await wirer.ConnectAsync(kind, null, null, "http://127.0.0.1:7879", CancellationToken.None);
    }

    private sealed class HealthyTester : IDownloadClientTester
    {
        public Task<(string HealthStatus, string Message, string? FailureCategory)> TestAsync(DownloadClientItem client, CancellationToken cancellationToken)
            => Task.FromResult<(string, string, string?)>(("healthy", "Connected.", null));
    }
}
