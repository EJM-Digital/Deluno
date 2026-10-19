using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Companions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Moq;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// What setting up qBittorrent and SABnzbd does about their first settings (#506), with the apps "installed" and a computer that writes to a
/// dictionary: it refuses while the app is running and still needs its first settings, picks a port that is free, generates a key or login,
/// keeps the login for Connect, binds only to this computer, and starts the app now and at sign-in.
/// </summary>
public sealed class CompanionFirstSettingsTests
{
    private static readonly string QbExe = Path.Combine(ProgramFilesDir, "qBittorrent", "qbittorrent.exe");
    private static readonly string QbIni = Path.Combine(RoamingDir, "qBittorrent", "qBittorrent.ini");
    private static readonly string SabExe = Path.Combine(ProgramFilesDir, "SABnzbd", "SABnzbd.exe");
    private static readonly string SabIni = Path.Combine(LocalDir, "sabnzbd", "sabnzbd.ini");
    // Built with Path.Combine so the same fake folders hold on Linux, where the code under test joins with "/".
    private const string ProgramFilesDir = "ProgramFiles";
    private const string RoamingDir = "Users/test/AppData/Roaming";
    private const string LocalDir = "Users/test/AppData/Local";

    private sealed class ScriptedSystem : ICompanionSystem
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> PortsInUse { get; } = [];
        public List<string> Written { get; } = [];
        public List<(string Path, string Arguments)> Started { get; } = [];
        public List<(string Name, string Command)> AtSignIn { get; } = [];

        public string ProgramFiles => ProgramFilesDir;
        public string RoamingAppData => RoamingDir;
        public string LocalAppData => LocalDir;
        public bool FileExists(string path) => Files.ContainsKey(path);
        public string? ReadText(string path) => Files.GetValueOrDefault(path);
        public void WriteText(string path, string contents) { Files[path] = contents; Written.Add(path); }
        public bool IsProcessRunning(string processName) => Running.Contains(processName);
        public void StopProcess(string processName) { }
        public bool IsPortInUse(int port) => PortsInUse.Contains(port);
        public IReadOnlyList<int> ListeningPortsOf(string folder) => [];
        public Task<CompanionRunResult> RunElevatedAsync(string path, string arguments, CancellationToken cancellationToken) => Task.FromResult(new CompanionRunResult(0, false));
        public Task<CompanionRunResult> RunAsync(string path, string arguments, CancellationToken cancellationToken) => Task.FromResult(new CompanionRunResult(0, false));
        public void Start(string path, string arguments) => Started.Add((path, arguments));
        public void StartAtSignIn(string name, string command) => AtSignIn.Add((name, command));
    }

    private sealed class KeptLogins : IInstalledClientLogins
    {
        public List<(string Kind, string? Username, string Secret)> Kept { get; } = [];
        public void Keep(string kind, string? username, string secret) => Kept.Add((kind, username, secret));
    }

    private sealed class Silent : ICompanionProgress
    {
        public void Report(string step, string message) { }
    }

    private static IConnectionsRepository NoConnections()
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return connections.Object;
    }

    private static CompanionRunContext Context() => new("http://127.0.0.1:5099", @"C:\temp", new Silent());

    private sealed class NoDownload : ICompanionDownloader
    {
        public Task<string> DownloadAsync(CompanionArtifact artifact, string folder, CancellationToken cancellationToken) => throw new InvalidOperationException("nothing is downloaded: the app is already installed");
    }

    private static (QbittorrentCompanion Tool, ScriptedSystem System, KeptLogins Logins) Qb()
    {
        var system = new ScriptedSystem();
        system.Files[QbExe] = "exe";
        var logins = new KeptLogins();
        return (new QbittorrentCompanion(system, NoConnections(), logins, new NoDownload()), system, logins);
    }

    private static (SabnzbdCompanion Tool, ScriptedSystem System, KeptLogins Logins) Sab()
    {
        var system = new ScriptedSystem();
        system.Files[SabExe] = "exe";
        var logins = new KeptLogins();
        return (new SabnzbdCompanion(system, NoConnections(), logins, new NoDownload()), system, logins);
    }

    [Fact]
    public async Task qBittorrent_that_is_running_and_has_no_Web_UI_yet_is_not_touched_and_the_person_is_asked_to_close_it()
    {
        var (tool, system, logins) = Qb();
        system.Running.Add("qbittorrent");

        var failure = await Assert.ThrowsAsync<CompanionStepException>(() => tool.RunAsync(Context(), CancellationToken.None));

        Assert.Equal("Close qBittorrent and run this again: its Web UI has to be switched on while it is closed.", failure.Message);
        Assert.Empty(system.Written);
        Assert.Empty(system.Started);
        Assert.Empty(logins.Kept);
    }

    [Fact]
    public async Task qBittorrent_gets_a_Web_UI_on_this_computer_only_with_a_generated_login_kept_for_Connect_and_is_started_now_and_at_sign_in()
    {
        var (tool, system, logins) = Qb();

        await tool.RunAsync(Context(), CancellationToken.None);

        var ini = system.Files[QbIni];
        Assert.Contains("WebUI\\Address=127.0.0.1", ini, StringComparison.Ordinal);
        Assert.Contains("WebUI\\Port=8080", ini, StringComparison.Ordinal);
        Assert.Contains("WebUI\\Username=deluno", ini, StringComparison.Ordinal);
        var kept = Assert.Single(logins.Kept);
        Assert.Equal(("qbittorrent", "deluno"), (kept.Kind, kept.Username));
        Assert.False(string.IsNullOrWhiteSpace(kept.Secret));
        Assert.DoesNotContain(kept.Secret, ini, StringComparison.Ordinal);
        // The stored hash is the kept password, hashed the way qBittorrent reads it.
        var stored = Regex.Match(ini, @"Password_PBKDF2=""@ByteArray\((?<salt>[^:]+):(?<key>[^)]+)\)""");
        Assert.True(stored.Success, "the password is stored as a PBKDF2 hash");
        var expected = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(kept.Secret), Convert.FromBase64String(stored.Groups["salt"].Value), 100_000, HashAlgorithmName.SHA512, 64);
        Assert.Equal(Convert.ToBase64String(expected), stored.Groups["key"].Value);
        Assert.Equal((QbExe, "--confirm-legal-notice"), Assert.Single(system.Started));
        Assert.Equal(("qBittorrent", $"\"{QbExe}\""), Assert.Single(system.AtSignIn));
    }

    [Fact]
    public async Task qBittorrent_takes_the_first_free_port_when_the_usual_ones_are_busy()
    {
        var (tool, system, _) = Qb();
        system.PortsInUse.UnionWith([8080, 8081, 8082]);

        await tool.RunAsync(Context(), CancellationToken.None);

        Assert.Contains("WebUI\\Port=8083", system.Files[QbIni], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SABnzbd_that_is_running_and_has_no_settings_yet_is_not_touched_and_the_person_is_asked_to_close_it()
    {
        var (tool, system, logins) = Sab();
        system.Running.Add("SABnzbd");

        var failure = await Assert.ThrowsAsync<CompanionStepException>(() => tool.RunAsync(Context(), CancellationToken.None));

        Assert.Equal("Close SABnzbd and run this again, so Deluno can give it settings before it starts.", failure.Message);
        Assert.Empty(system.Written);
        Assert.Empty(system.Started);
        Assert.Empty(logins.Kept);
    }

    [Fact]
    public async Task SABnzbd_gets_first_settings_on_a_free_port_for_this_computer_only_with_a_generated_key_kept_for_Connect_and_is_started_now_and_at_sign_in()
    {
        var (tool, system, logins) = Sab();
        system.PortsInUse.Add(8085);

        await tool.RunAsync(Context(), CancellationToken.None);

        var ini = system.Files[SabIni];
        Assert.Contains("host = 127.0.0.1", ini, StringComparison.Ordinal);
        Assert.Contains("port = 8086", ini, StringComparison.Ordinal);
        var kept = Assert.Single(logins.Kept);
        Assert.Equal(("sabnzbd", (string?)null), (kept.Kind, kept.Username));
        Assert.Matches("^[0-9a-f]{32}$", kept.Secret);
        Assert.Contains($"api_key = {kept.Secret}", ini, StringComparison.Ordinal);
        var arguments = $"-f \"{SabIni}\" -b 0";
        Assert.Equal((SabExe, arguments), Assert.Single(system.Started));
        Assert.Equal(("SABnzbd", $"\"{SabExe}\" {arguments}"), Assert.Single(system.AtSignIn));
    }

    [Fact]
    public async Task SABnzbd_that_already_has_settings_keeps_them_and_only_its_key_is_kept_for_Connect_even_while_it_is_running()
    {
        var (tool, system, logins) = Sab();
        system.Files[SabIni] = "[misc]\nhost = 0.0.0.0\nport = 8090\napi_key = abc123\n";
        system.Running.Add("SABnzbd");

        await tool.RunAsync(Context(), CancellationToken.None);

        Assert.Empty(system.Written);
        Assert.Equal(("sabnzbd", (string?)null, "abc123"), Assert.Single(logins.Kept));
        Assert.Empty(system.Started);
        Assert.Single(system.AtSignIn);
    }
}
