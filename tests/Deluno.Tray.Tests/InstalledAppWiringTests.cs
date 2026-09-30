using System.Security.Cryptography;
using System.Text;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1054: what the tray leaves behind so the running Deluno can connect each installed app.</summary>
public sealed partial class ComponentSetupTests
{
    [Theory]
    [InlineData("qbittorrent", 8081)]
    [InlineData("transmission", 9091)]
    [InlineData("deluge", 8112)]
    [InlineData("sabnzbd", 8080)]
    [InlineData("nzbget", 6789)]
    [InlineData("weir", 9347)]
    public async Task An_installed_app_is_recorded_for_connecting_with_its_own_port(string kind, int port)
    {
        var record = new RecordingInstalledRecord();

        var result = await InstallWithRecord(Load(kind), record, new RecordingCredentialStore(), template: "ControlIP=0.0.0.0\nMainDir=${AppDir}\\downloads\nControlUsername=nzbget\nControlPassword=tegbzn6789\n");

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        var entry = Assert.Single(record.Entries);
        Assert.Equal((kind, "localhost", port), (entry.Kind, entry.Host, entry.Port));
    }

    [Fact]
    public async Task An_app_that_was_not_installed_is_not_recorded()
    {
        var record = new RecordingInstalledRecord();
        var service = new ComponentInstallService(
            new AlreadyThere(), new FakePorts(), new FixedDownloader(WriteTempFile(new byte[20], ".exe")), new AlwaysSigned(), new ElevatedRecordingRunner(),
            new RecordingStarter(), new ReadyChecker(), new FakeConfigWriter(true), installedRecord: record, credentialCaptureWait: TimeSpan.Zero);

        var result = await service.InstallAsync(Load("qbittorrent"), CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.AlreadyInstalled, result.Outcome);
        Assert.Empty(record.Entries);
    }

    [Fact]
    public async Task SABnzbds_own_api_key_is_read_from_its_settings_after_it_starts_and_kept_for_Deluno()
    {
        var store = new RecordingCredentialStore();

        await InstallWithRecord(Load("sabnzbd"), new RecordingInstalledRecord(), store, template: "[misc]\nhost = 127.0.0.1\napi_key = 27e9b9b2630c4cc9a83437755424919d\nnzb_key = other\n");

        var saved = store.Saved["sabnzbd"];
        Assert.Equal("27e9b9b2630c4cc9a83437755424919d", saved.Password);
        Assert.Equal(string.Empty, saved.Username);
    }

    [Fact]
    public async Task A_SABnzbd_settings_file_with_no_key_yet_saves_nothing()
    {
        var store = new RecordingCredentialStore();

        await InstallWithRecord(Load("sabnzbd"), new RecordingInstalledRecord(), store, template: "[misc]\napi_key = \n");

        Assert.False(store.Has("sabnzbd"));
    }

    [Fact]
    public void SABnzbd_key_parsing_reads_only_the_misc_section_and_ignores_blank_values()
    {
        Assert.Equal("abc", SabnzbdIni.ApiKey("__version__ = 19\n[misc]\r\napi_key = abc\r\n[servers]\napi_key = nope\n"));
        Assert.Equal("quoted", SabnzbdIni.ApiKey("[misc]\napi_key = \"quoted\"\n"));
        Assert.Null(SabnzbdIni.ApiKey("[misc]\napi_key = \"\"\n"));
        Assert.Null(SabnzbdIni.ApiKey("[other]\napi_key = abc\n"));
        Assert.Null(SabnzbdIni.ApiKey(null));
    }

    [Fact]
    public async Task Deluge_gets_no_generated_login_only_its_local_address_and_port()
    {
        var writer = new FakeConfigWriter(true);
        var store = new RecordingCredentialStore();

        await InstallWith(Load("deluge"), writer, store);

        Assert.False(store.Has("deluge"));
        Assert.Empty(store.Saved);
        var content = writer.Content!;
        Assert.Contains("\"interface\": \"127.0.0.1\"", content, StringComparison.Ordinal);
        Assert.Contains("\"port\": 8112", content, StringComparison.Ordinal);
        Assert.DoesNotContain("pwd_", content, StringComparison.Ordinal);
        Assert.DoesNotContain("first_login", content, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", content, StringComparison.Ordinal);
    }

    private static async Task<ComponentInstallResult> InstallWithRecord(BundledComponentSpec real, RecordingInstalledRecord record, RecordingCredentialStore store, string? template = null)
    {
        var service = new ComponentInstallService(
            new FakePrograms(), new FakePorts(), new FixedDownloader(WriteTempFile(new byte[20], ".exe")), new AlwaysSigned(), new ElevatedRecordingRunner(),
            new RecordingStarter(), new ReadyChecker(), new FakeConfigWriter(true), templates: new FixedTemplate(template),
            credentialStore: store, startedComponents: new FakeRegistry(), installedRecord: record, credentialCaptureWait: TimeSpan.Zero);
        var spec = real with
        {
            Url = "https://example.invalid/installer.exe", GithubRepo = null, DirectoryListingUrl = null,
            Sha256 = null, MustBeSigned = true, ExpectedPublisher = "Test Publisher", MinimumBytes = 1, Prerequisite = null, ReadinessTimeoutSeconds = 1
        };
        return await service.InstallAsync(spec, CancellationToken.None);
    }

    private sealed class RecordingInstalledRecord : IInstalledComponentRecord
    {
        public List<InstalledComponentEntry> Entries { get; } = [];
        public IReadOnlyList<InstalledComponentEntry> Read() => Entries;
        public void MarkInstalled(string kind, string host, int port) => Entries.Add(new InstalledComponentEntry(kind, host, port, DateTimeOffset.UtcNow));
        public void MarkWired(string kind) { }
    }

    private sealed class AlreadyThere : IInstalledProgramsReader
    {
        public IReadOnlyList<string> ReadDisplayNames() => ["qBittorrent 5.2.1"];
    }
}
