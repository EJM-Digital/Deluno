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

        var result = await InstallWithRecord(Load(kind), record, new RecordingCredentialStore(), template: "ControlIP=0.0.0.0\nMainDir=${AppDir}\\downloads\nControlUsername=nzbget\nControlPassword=tegbzn6789\nServer1.Active=yes\n");

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        var entry = Assert.Single(record.Entries);
        Assert.Equal((kind, "localhost", port), (entry.Kind, entry.Host, entry.Port));
    }

    [Fact]
    public async Task The_install_is_recorded_before_the_generated_sign_in_is_kept_so_there_is_no_gap_between_them()
    {
        var record = new RecordingInstalledRecord();
        var store = new OrderCheckingStore(() => record.Entries.Count);

        await InstallWithRecord(Load("qbittorrent"), record, store);

        Assert.Equal(1, store.EntriesWhenSaved);
    }

    [Fact]
    public async Task A_failed_write_of_the_install_record_is_tried_again()
    {
        var record = new FlakyInstalledRecord(failures: 2);

        var result = await InstallWithRecord(Load("nzbget"), record, new RecordingCredentialStore(), template: "ControlIP=0.0.0.0");

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        Assert.Single(record.Entries);
        Assert.Equal(3, record.Attempts);
    }

    private sealed class FlakyInstalledRecord(int failures) : RecordingInstalledRecord
    {
        public int Attempts { get; private set; }
        public override void MarkInstalled(string kind, string host, int port)
        {
            if (++Attempts <= failures)
            {
                throw new IOException("locked");
            }

            base.MarkInstalled(kind, host, port);
        }
    }

    private sealed class OrderCheckingStore(Func<int> entries) : IBundledComponentCredentialStore
    {
        public int? EntriesWhenSaved { get; private set; }
        private readonly Dictionary<string, BundledComponentCredentials> _saved = new();
        public bool Has(string kind) => _saved.ContainsKey(kind);
        public BundledComponentCredentials? TryGet(string kind) => _saved.GetValueOrDefault(kind);
        public void Save(string kind, BundledComponentCredentials credentials)
        {
            EntriesWhenSaved = entries();
            _saved[kind] = credentials;
        }

        public void Remove(string kind) => _saved.Remove(kind);
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
    public async Task Deluge_gets_a_generated_web_password_kept_for_the_connection_and_never_the_shipped_default()
    {
        var writer = new FakeConfigWriter(true);
        var store = new RecordingCredentialStore();

        await InstallWith(Load("deluge"), writer, store);

        Assert.True(store.Has("deluge"));
        var saved = store.Saved.Single().Value;
        Assert.NotEqual("deluge", saved.Password);
        Assert.True(saved.Password.Length >= 20);
        var content = writer.Content!;
        Assert.Contains("\"interface\": \"127.0.0.1\"", content, StringComparison.Ordinal);
        Assert.Contains("\"port\": 8112", content, StringComparison.Ordinal);
        Assert.Contains("\"first_login\": false", content, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", content, StringComparison.Ordinal);
        Assert.DoesNotContain(saved.Password, content, StringComparison.Ordinal);

        // Deluge checks sha1(salt + password) as hex against the two values written to its web.conf.
        var json = content[(content.IndexOf("}{", StringComparison.Ordinal) + 1)..];
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var salt = document.RootElement.GetProperty("pwd_salt").GetString()!;
        var sha1 = document.RootElement.GetProperty("pwd_sha1").GetString()!;
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(salt + saved.Password))).ToLowerInvariant(), sha1);
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(salt + "deluge"))).ToLowerInvariant(), sha1);
    }

    private static async Task<ComponentInstallResult> InstallWithRecord(BundledComponentSpec real, RecordingInstalledRecord record, IBundledComponentCredentialStore store, string? template = null)
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

    private class RecordingInstalledRecord : IInstalledComponentRecord
    {
        public List<InstalledComponentEntry> Entries { get; } = [];
        public IReadOnlyList<InstalledComponentEntry> Read() => Entries;
        public virtual void MarkInstalled(string kind, string host, int port) => Entries.Add(new InstalledComponentEntry(kind, host, port, DateTimeOffset.UtcNow));
        public void MarkWired(string kind) { }
    }

    private sealed class AlreadyThere : IInstalledProgramsReader
    {
        public IReadOnlyList<string> ReadDisplayNames() => ["qBittorrent 5.2.1"];
    }
}
