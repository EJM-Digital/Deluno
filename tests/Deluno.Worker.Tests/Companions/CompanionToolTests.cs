using System.Security.Cryptography;
using System.Text;
using Deluno.Companions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Moq;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// #506: the parts of setting up SABnzbd, qBittorrent and Weir that can be
/// proved without installing anything - what is written, what is verified, and
/// what Deluno refuses to do to a tool somebody already set up.
/// </summary>
public sealed class CompanionToolTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-companion-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_qBittorrent_password_is_stored_the_way_qBittorrent_reads_it()
    {
        var salt = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();

        var hash = CompanionConfigFiles.QbittorrentPasswordHash("correct horse", salt);

        var expectedKey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("correct horse"), salt, 100_000, HashAlgorithmName.SHA512, 64);
        Assert.Equal($"@ByteArray({Convert.ToBase64String(salt)}:{Convert.ToBase64String(expectedKey)})", hash);
        var ini = CompanionConfigFiles.QbittorrentIni(8080, "deluno", hash);
        Assert.Contains("[LegalNotice]\nAccepted=true", ini.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("WebUI\\Address=127.0.0.1", ini, StringComparison.Ordinal);
        Assert.Contains($"WebUI\\Password_PBKDF2=\"{hash}\"", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_SABnzbd_settings_file_gives_up_its_key_and_port()
    {
        var ini = "[misc]\nhost = 0.0.0.0\nport = 8090\napi_key = abc123\n[servers]\n[[news]]\nport = 563\n";

        Assert.Equal("abc123", CompanionConfigFiles.SabnzbdValue(ini, "api_key"));
        Assert.Equal("8090", CompanionConfigFiles.SabnzbdValue(ini, "port"));
        Assert.Null(CompanionConfigFiles.SabnzbdValue(null, "api_key"));
        Assert.Contains("host = 127.0.0.1", CompanionConfigFiles.SabnzbdIni(8085, "k", "n"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_download_that_is_not_the_pinned_build_is_refused()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "setup.exe");
        File.WriteAllBytes(path, new byte[2048]);

        var tooSmall = CompanionDownloader.Verify(new CompanionArtifact("setup.exe", new Uri("https://example.test/setup.exe"), "00", false, 1_000_000), path);
        var wrongHash = CompanionDownloader.Verify(new CompanionArtifact("setup.exe", new Uri("https://example.test/setup.exe"), new string('A', 64), false, 1), path);
        var right = CompanionDownloader.Verify(new CompanionArtifact("setup.exe", new Uri("https://example.test/setup.exe"), Convert.ToHexString(SHA256.HashData(new byte[2048])), false, 1), path);

        Assert.Contains("too small", tooSmall, StringComparison.Ordinal);
        Assert.Contains("SHA-256 does not match", wrongHash, StringComparison.Ordinal);
        Assert.Null(right);
    }

    /// <summary>#1061: the older "Get the tools you need" path never runs an installer straight from the download folder.</summary>
    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("sabnzbd")]
    public async Task An_installer_is_run_from_a_locked_folder_after_its_pinned_hash_is_checked_there(string kind)
    {
        var system = new FakeSystem(installed: false);
        ICompanionTool tool = kind == "qbittorrent"
            ? new QbittorrentCompanion(system, NoClients(), Mock.Of<IInstalledClientLogins>(), new StubDownloader(_folder))
            : new SabnzbdCompanion(system, NoClients(), Mock.Of<IInstalledClientLogins>(), new StubDownloader(_folder));

        try
        {
            await tool.RunAsync(Context(), CancellationToken.None);
        }
        catch (CompanionStepException)
        {
            // The fake reports the app as still missing afterwards; only the installer launch is being checked.
        }

        Assert.Equal(1, system.Elevated);
        Assert.Equal(ElevatedInstallScript.PowerShellPath, system.ElevatedPath);
        var arguments = system.ElevatedArguments!;
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[(arguments.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + 16)..]));
        var artifact = kind == "qbittorrent" ? CompanionCatalog.Qbittorrent : CompanionCatalog.Sabnzbd;
        Assert.Contains($"'{artifact.Sha256}'", script, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath", script, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $copy -ArgumentList '/S'", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $fresh -Recurse", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declined_permission_prompt_installs_nothing_and_says_so()
    {
        var system = new FakeSystem(installed: false) { Declines = true };
        var tool = new QbittorrentCompanion(system, NoClients(), Mock.Of<IInstalledClientLogins>(), new StubDownloader(_folder));

        var failure = await Assert.ThrowsAsync<CompanionStepException>(() => tool.RunAsync(Context(), CancellationToken.None));

        Assert.Contains("was not given. Nothing was installed.", failure.Message, StringComparison.Ordinal);
        Assert.Empty(system.Written);
    }

    /// <summary>A qBittorrent somebody already gave a Web UI login keeps it. Deluno does not overwrite it to connect.</summary>
    [Fact]
    public async Task An_existing_qBittorrent_login_is_never_overwritten()
    {
        var system = new FakeSystem(installed: true);
        system.Files[Path.Combine(system.RoamingAppData, "qBittorrent", "qBittorrent.ini")] = "[Preferences]\nWebUI\\Username=owner\n";
        var tool = new QbittorrentCompanion(system, NoClients(), Mock.Of<IInstalledClientLogins>(), new StubDownloader(_folder));

        var failure = await Assert.ThrowsAsync<CompanionStepException>(() => tool.RunAsync(Context(), CancellationToken.None));

        Assert.Contains("already has a Web UI login", failure.Message, StringComparison.Ordinal);
        Assert.Empty(system.Written);
        Assert.Equal(0, system.Elevated);
    }

    [Fact]
    public async Task A_tool_already_connected_is_left_as_it_is()
    {
        var connections = new Mock<IConnectionsRepository>();
        var client = (DownloadClientItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DownloadClientItem));
        connections.Setup(repo => repo.ListDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([client with { Name = "SABnzbd", Protocol = "sabnzbd", Host = "127.0.0.1" }]);
        var system = new FakeSystem(installed: false);
        var progress = new RecordingProgress();
        var tool = new SabnzbdCompanion(system, connections.Object, Mock.Of<IInstalledClientLogins>(), new StubDownloader(_folder));

        await tool.RunAsync(new CompanionRunContext("http://127.0.0.1:7879", _folder, progress), CancellationToken.None);

        Assert.Contains("already connected to Deluno as “SABnzbd”", progress.Messages.Last(), StringComparison.Ordinal);
        Assert.Equal(0, system.Elevated);
    }


    [Fact]
    public void An_installed_qBittorrent_and_SABnzbd_say_where_they_answer_for_an_Open_link()
    {
        var system = new FakeSystem(installed: true);
        system.Files[Path.Combine(system.RoamingAppData, "qBittorrent", "qBittorrent.ini")] = "[Preferences]\nWebUI\\Port=8081\n";
        system.Files[Path.Combine(system.LocalAppData, "sabnzbd", "sabnzbd.ini")] = "[misc]\nport = 8085\n";

        ICompanionTool qbit = new QbittorrentCompanion(system, NoClients(), Mock.Of<IInstalledClientLogins>(), new StubDownloader(_folder));
        ICompanionTool sab = new SabnzbdCompanion(system, NoClients(), Mock.Of<IInstalledClientLogins>(), new StubDownloader(_folder));

        Assert.Equal("http://localhost:8081", qbit.LocalUrl());
        Assert.Equal("http://localhost:8085", sab.LocalUrl());
    }
    /// <summary>#553: Deluno starting Weir never opens a browser window on the computer Deluno runs on.</summary>
    [Fact]
    public void Weir_is_started_without_opening_a_browser()
    {
        Assert.Equal("--port 9347 --no-browser", WeirCompanion.StartArguments(9347));
        Assert.Equal("--no-browser", WeirCompanion.StartArguments(port: null));
    }

    private CompanionRunContext Context() =>new("http://127.0.0.1:7879", _folder, new RecordingProgress());

    private static IConnectionsRepository NoClients()
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return connections.Object;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RecordingProgress : ICompanionProgress
    {
        public List<string> Messages { get; } = [];

        public void Report(string step, string message) => Messages.Add(message);
    }

    /// <summary>Hands back an "installer" without touching the network.</summary>
    private sealed class StubDownloader(string folder) : ICompanionDownloader
    {
        public Task<string> DownloadAsync(CompanionArtifact artifact, string target, CancellationToken cancellationToken)
            => Task.FromResult(Path.Combine(folder, artifact.FileName));
    }

    private sealed class FakeSystem(bool installed) : ICompanionSystem
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Written { get; } = [];
        public bool Declines { get; init; }
        public int Elevated { get; private set; }
        public string? ElevatedPath { get; private set; }
        public string? ElevatedArguments { get; private set; }

        public string ProgramFiles => @"C:\Program Files";
        public string RoamingAppData => @"C:\Users\test\AppData\Roaming";
        public string LocalAppData => @"C:\Users\test\AppData\Local";

        public bool FileExists(string path) => installed || Files.ContainsKey(path);
        public string? ReadText(string path) => Files.GetValueOrDefault(path);

        public void WriteText(string path, string contents)
        {
            Files[path] = contents;
            Written.Add(path);
        }

        public bool IsProcessRunning(string processName) => false;
        public bool IsPortInUse(int port) => false;
        public IReadOnlyList<int> ListeningPortsOf(string folder) => [];

        public Task<CompanionRunResult> RunElevatedAsync(string path, string arguments, CancellationToken cancellationToken)
        {
            Elevated++;
            ElevatedPath = path;
            ElevatedArguments = arguments;
            return Task.FromResult(new CompanionRunResult(Declines ? 1223 : 0, Declines));
        }

        public Task<CompanionRunResult> RunAsync(string path, string arguments, CancellationToken cancellationToken) => Task.FromResult(new CompanionRunResult(0, false));
        public void Start(string path, string arguments) { }
        public void StartAtSignIn(string name, string command) { }
    }
}
