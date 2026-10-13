using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1177: component state lives in the data root, so a reinstall (which replaces the install root) cannot take it.</summary>
public sealed class ComponentDataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-component-folder-" + Guid.NewGuid().ToString("N"));
    private string InstallRoot => Path.Combine(_root, "local", "Deluno");
    private string DataRoot => Path.Combine(_root, "local", "DelunoData");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp folder */ }
    }

    private static Task<ComponentStateMigrationResult> Run(string install, string data, string[] installed, Func<HttpMessageHandler>? rpc, Registry? registry = null, BundledComponentCredentials? login = null)
        => ComponentStateMigration.RunAsync(install, data, installed, login, registry, 9091, rpc, null, CancellationToken.None);

    private sealed class Registry : IStartedComponentsRegistry
    {
        public List<string> Kinds { get; } = [];
        public IReadOnlyList<string> Read() => Kinds;
        public void Add(string kind) => Kinds.Add(kind);
    }

    /// <summary>A Transmission that answers session-get, torrent-get and torrent-add, or nothing at all.</summary>
    private sealed class FakeTransmission(bool running) : HttpMessageHandler
    {
        public List<string> Added { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!running)
            {
                throw new HttpRequestException("connection refused");
            }

            if (!request.Headers.Contains("X-Transmission-Session-Id"))
            {
                var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                conflict.Headers.Add("X-Transmission-Session-Id", "abc");
                return Task.FromResult(conflict);
            }

            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            var method = body.RootElement.GetProperty("method").GetString();
            var arguments = method switch
            {
                "session-get" => """{"download-dir":"/mnt/finished","incomplete-dir":"/mnt/partial/Transmission","incomplete-dir-enabled":true}""",
                "torrent-get" => """{"torrents":[{"name":"Film A","magnetLink":"magnet:?xt=urn:btih:aaa","downloadDir":"/mnt/finished/Movies"},{"name":"No link","magnetLink":""}]}""",
                _ => "{}"
            };
            if (method == "torrent-add")
            {
                Added.Add(body.RootElement.GetProperty("arguments").GetProperty("filename").GetString()!);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":\"success\",\"arguments\":" + arguments + "}", Encoding.UTF8, "application/json")
            });
        }
    }

    private const string SeedSettings = "{\"download-dir\":\"D:\\\\Mine\"}";

    private static void Seed(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "resume"));
        Directory.CreateDirectory(Path.Combine(folder, "torrents"));
        File.WriteAllText(Path.Combine(folder, "settings.json"), SeedSettings);
        File.WriteAllText(Path.Combine(folder, "resume", "x.resume"), "resume-data");
        File.WriteAllText(Path.Combine(folder, "torrents", "x.torrent"), "torrent-data");
    }

    [Fact]
    public void Transmission_is_started_with_a_folder_in_the_data_root_not_the_install_root()
    {
        var transmission = BundledComponents.Load().Single(s => s.Kind == "transmission");

        Assert.StartsWith("%DelunoData%\\components\\transmission", transmission.ConfigFilePath);
        Assert.Contains("%DelunoData%\\components\\transmission", transmission.PostInstallArgs);
        Assert.DoesNotContain("LocalAppData", transmission.PostInstallArgs, StringComparison.OrdinalIgnoreCase);
        Assert.All(BundledComponents.Load(), s =>
        {
            Assert.DoesNotContain("%LocalAppData%\\Deluno\\", s.ConfigFilePath ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%LocalAppData%\\Deluno\\", s.PostInstallArgs ?? "", StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void The_data_folder_token_expands_to_the_data_root_for_the_daemon_arguments()
    {
        var original = ComponentPaths.DataRoot;
        try
        {
            ComponentPaths.DataRoot = () => DataRoot;
            var transmission = BundledComponents.Load().Single(s => s.Kind == "transmission");

            var args = ComponentAutoStarter.ExpandArgs(transmission.PostInstallArgs)!;

            Assert.Contains(Path.Combine(DataRoot, "components", "transmission"), args);
            Assert.DoesNotContain("%", args);
        }
        finally
        {
            ComponentPaths.DataRoot = original;
        }
    }

    [Fact]
    public async Task A_reinstall_that_replaces_the_install_root_leaves_the_component_folder_alone()
    {
        var folder = ComponentPaths.ComponentFolder(DataRoot, "transmission");
        Seed(folder);
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllText(Path.Combine(InstallRoot, "Deluno.exe"), "old build");

        // Velopack's Setup replaces the whole install root.
        Directory.Delete(InstallRoot, recursive: true);
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllText(Path.Combine(InstallRoot, "Deluno.exe"), "new build");

        var result = await Run(InstallRoot, DataRoot, ["transmission"], () => new FakeTransmission(running: false));

        Assert.False(result.TransmissionFolderRecreated);
        Assert.Empty(result.Moved);
        Assert.Equal(SeedSettings, File.ReadAllText(Path.Combine(folder, "settings.json")));
        Assert.Equal("resume-data", File.ReadAllText(Path.Combine(folder, "resume", "x.resume")));
        Assert.Equal("torrent-data", File.ReadAllText(Path.Combine(folder, "torrents", "x.torrent")));
        Assert.Null(TransmissionReadd.Pending(DataRoot));
    }

    [Fact]
    public async Task A_folder_still_in_the_install_root_is_moved_to_the_data_root_with_nothing_lost()
    {
        var old = Path.Combine(InstallRoot, "components", "transmission");
        Seed(old);
        Directory.CreateDirectory(Path.Combine(InstallRoot, "config"));
        File.WriteAllText(Path.Combine(InstallRoot, "config", "started-components.json"), "[\"transmission\"]");

        var result = await Run(InstallRoot, DataRoot, ["transmission"], () => new FakeTransmission(running: false));

        var moved = ComponentPaths.ComponentFolder(DataRoot, "transmission");
        Assert.Equal(["transmission"], result.Moved);
        Assert.False(result.TransmissionFolderRecreated);
        Assert.Equal("resume-data", File.ReadAllText(Path.Combine(moved, "resume", "x.resume")));
        Assert.Equal("torrent-data", File.ReadAllText(Path.Combine(moved, "torrents", "x.torrent")));
        Assert.Equal(SeedSettings, File.ReadAllText(Path.Combine(moved, "settings.json")));
        Assert.False(Directory.Exists(old));
        Assert.Equal("[\"transmission\"]", File.ReadAllText(Path.Combine(DataRoot, "config", "started-components.json")));
        Assert.False(File.Exists(Path.Combine(InstallRoot, "config", "started-components.json")));
    }

    [Fact]
    public async Task A_folder_in_both_places_is_merged_and_the_newer_files_are_kept()
    {
        var old = Path.Combine(InstallRoot, "components", "transmission");
        Seed(old);
        var current = ComponentPaths.ComponentFolder(DataRoot, "transmission");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "settings.json"), "newer");

        await Run(InstallRoot, DataRoot, ["transmission"], null);

        Assert.Equal("newer", File.ReadAllText(Path.Combine(current, "settings.json")));
        Assert.Equal("resume-data", File.ReadAllText(Path.Combine(current, "resume", "x.resume")));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public async Task When_both_folders_are_gone_and_Transmission_is_running_its_folders_are_written_again_and_its_torrents_kept_for_re_adding()
    {
        var registry = new Registry();

        var result = await Run(InstallRoot, DataRoot, ["transmission"], () => new FakeTransmission(running: true), registry);

        Assert.True(result.TransmissionFolderRecreated);
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(ComponentPaths.ComponentFolder(DataRoot, "transmission"), "settings.json")));
        var root = settings.RootElement;
        Assert.Equal("/mnt/finished", root.GetProperty("download-dir").GetString());
        Assert.Equal("/mnt/partial/Transmission", root.GetProperty("incomplete-dir").GetString());
        Assert.True(root.GetProperty("incomplete-dir-enabled").GetBoolean());
        Assert.Equal("127.0.0.1", root.GetProperty("rpc-bind-address").GetString());
        Assert.False(root.GetProperty("rpc-authentication-required").GetBoolean());
        Assert.Equal(["transmission"], registry.Kinds);

        var notice = TransmissionReadd.Pending(DataRoot)!;
        Assert.True(notice.DaemonNeedsRestart);
        var torrent = Assert.Single(notice.Torrents);
        Assert.Equal("magnet:?xt=urn:btih:aaa", torrent.MagnetLink);
        Assert.Equal("/mnt/finished/Movies", torrent.DownloadDir);
    }

    [Fact]
    public async Task When_both_folders_are_gone_and_Transmission_is_not_running_the_saved_folders_and_sign_in_are_used()
    {
        DownloadLayoutRecord.Save(DataRoot, new DownloadLayout("E:\\Done", "E:\\Partial"));

        await Run(InstallRoot, DataRoot, ["transmission"], () => new FakeTransmission(running: false), login: new BundledComponentCredentials("deluno", "secret-pass"));

        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(ComponentPaths.ComponentFolder(DataRoot, "transmission"), "settings.json")));
        var root = settings.RootElement;
        Assert.Equal("E:\\Done", root.GetProperty("download-dir").GetString());
        Assert.Equal(Path.Combine("E:\\Partial", "Transmission"), root.GetProperty("incomplete-dir").GetString());
        Assert.True(root.GetProperty("rpc-authentication-required").GetBoolean());
        Assert.Equal("deluno", root.GetProperty("rpc-username").GetString());

        var notice = TransmissionReadd.Pending(DataRoot)!;
        Assert.False(notice.DaemonNeedsRestart);
        Assert.Empty(notice.Torrents);
    }

    // ---- #1260: a record that cannot be read is not "no record"; Transmission's settings are not written from a guess.

    [Theory]
    [InlineData("{\"CompletedRoot\":\"E:\\Do")]
    [InlineData("")]
    public async Task A_damaged_download_layout_record_stops_Transmission_being_written_again_with_default_folders(string damaged)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DownloadLayoutRecord.PathFor(DataRoot))!);
        File.WriteAllText(DownloadLayoutRecord.PathFor(DataRoot), damaged);

        var result = await Run(InstallRoot, DataRoot, ["transmission"], () => new FakeTransmission(running: false), login: new BundledComponentCredentials("deluno", "secret-pass"));

        Assert.False(result.TransmissionFolderRecreated);
        Assert.False(File.Exists(Path.Combine(ComponentPaths.ComponentFolder(DataRoot, "transmission"), "settings.json")));
        Assert.Null(TransmissionReadd.Pending(DataRoot));
        Assert.Equal(damaged, File.ReadAllText(DownloadLayoutRecord.PathFor(DataRoot)));
    }

    [Fact]
    public async Task A_missing_download_layout_record_still_gets_the_default_folders_as_before()
    {
        var result = await Run(InstallRoot, DataRoot, ["transmission"], () => new FakeTransmission(running: false));

        Assert.True(result.TransmissionFolderRecreated);
        Assert.True(File.Exists(Path.Combine(ComponentPaths.ComponentFolder(DataRoot, "transmission"), "settings.json")));
    }

    [Fact]
    public async Task A_component_that_was_never_installed_gets_no_folder_and_no_notice()
    {
        var result = await Run(InstallRoot, DataRoot, [], () => new FakeTransmission(running: false));

        Assert.False(result.TransmissionFolderRecreated);
        Assert.False(Directory.Exists(ComponentPaths.ComponentFolder(DataRoot, "transmission")));
        Assert.Null(TransmissionReadd.Pending(DataRoot));
    }

    [Fact]
    public async Task Adding_the_torrents_back_restarts_the_daemon_adds_each_magnet_and_clears_the_notice()
    {
        await Run(InstallRoot, DataRoot, ["transmission"], () => new FakeTransmission(running: true));
        var transmission = new FakeTransmission(running: true);
        var restarts = 0;

        var message = await TransmissionReadd.RunAsync(DataRoot, new TransmissionRpc(9091, null, () => transmission), _ => Task.FromResult(++restarts > 0), CancellationToken.None);

        Assert.Equal(1, restarts);
        Assert.Equal(["magnet:?xt=urn:btih:aaa"], transmission.Added);
        Assert.Equal("Deluno added 1 torrents back to Transmission.", message);
        Assert.Null(TransmissionReadd.Pending(DataRoot));
    }

    [Fact]
    public async Task Adding_the_torrents_back_says_so_when_Transmission_is_not_running_and_keeps_the_notice()
    {
        await Run(InstallRoot, DataRoot, ["transmission"], () => new FakeTransmission(running: false));

        var message = await TransmissionReadd.RunAsync(DataRoot, new TransmissionRpc(9091, null, () => new FakeTransmission(running: false)), _ => Task.FromResult(true), CancellationToken.None);

        Assert.Contains("Transmission is not running", message);
        Assert.NotNull(TransmissionReadd.Pending(DataRoot));
    }
}
