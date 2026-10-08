using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1177: a daemon already running from the install folder when a reinstall removes it is moved over automatically.</summary>
public sealed class RunningDaemonMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-running-daemon-" + Guid.NewGuid().ToString("N"));
    private string Install => Path.Combine(_root, "Deluno");
    private string Data => Path.Combine(_root, "DelunoData");
    private string OldFolder => Path.Combine(Install, "components", "transmission");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp folder */ }
    }

    private sealed class FakeDaemon(string oldFolder) : ITransmissionDaemonControl
    {
        public string CommandLine { get; set; } = "";
        public bool Started { get; private set; }
        public bool Killed { get; private set; }
        public bool ExitsByItself { get; set; } = true;
        public string? FolderWhenClosed { get; private set; }
        public Action? OnClosing { get; set; }

        public Task<IReadOnlyList<string>> CommandLinesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(CommandLine.Length == 0 ? [] : [CommandLine]);

        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(ExitsByItself);

        public Task KillAsync(CancellationToken cancellationToken)
        {
            Killed = true;
            return Task.CompletedTask;
        }

        public Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            FolderWhenClosed = Directory.Exists(oldFolder) ? "old folder still there" : "old folder gone";
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRpc(FakeDaemon daemon, bool reloadsEverything) : HttpMessageHandler
    {
        public List<string> Added { get; } = [];
        public bool Closed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains("X-Transmission-Session-Id"))
            {
                var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                conflict.Headers.Add("X-Transmission-Session-Id", "s");
                return Task.FromResult(conflict);
            }

            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            var method = body.RootElement.GetProperty("method").GetString();
            var arguments = "{}";
            if (method == "session-close")
            {
                Closed = true;
                daemon.OnClosing?.Invoke();
            }
            else if (method == "torrent-add")
            {
                Added.Add(body.RootElement.GetProperty("arguments").GetProperty("filename").GetString()! + "|" + body.RootElement.GetProperty("arguments").GetProperty("download-dir").GetString());
            }
            else if (method == "torrent-get")
            {
                var torrents = new List<string> { "{\"name\":\"A\",\"magnetLink\":\"magnet:?xt=urn:btih:aaa\",\"downloadDir\":\"/d/Movies\"}" };
                if (!daemon.Started || reloadsEverything)
                {
                    torrents.Add("{\"name\":\"B\",\"magnetLink\":\"magnet:?xt=urn:btih:bbb\",\"downloadDir\":\"/d/TV\"}");
                }

                arguments = "{\"torrents\":[" + string.Join(",", torrents) + "]}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":\"success\",\"arguments\":" + arguments + "}", Encoding.UTF8, "application/json") });
        }
    }

    private (FakeDaemon Daemon, FakeRpc Rpc, TransmissionRpc Client) Setup(bool reloadsEverything = false)
    {
        var daemon = new FakeDaemon(OldFolder) { CommandLine = $"\"C:\\Program Files\\Transmission\\transmission-daemon.exe\" -f -g \"{OldFolder}\" -r 127.0.0.1" };
        var rpc = new FakeRpc(daemon, reloadsEverything);
        return (daemon, rpc, new TransmissionRpc(9091, null, () => rpc));
    }

    [Theory]
    [InlineData("daemon.exe -f -g \"C:\\Users\\a b\\x\" -r 127.0.0.1", "C:\\Users\\a b\\x")]
    [InlineData("daemon.exe -f -g C:\\x\\y -r 127.0.0.1", "C:\\x\\y")]
    [InlineData("daemon.exe --config-dir=C:\\z", "C:\\z")]
    [InlineData("daemon.exe -f", null)]
    public void The_config_folder_is_read_from_the_command_line(string commandLine, string? expected)
        => Assert.Equal(expected, RunningDaemonMigration.ConfigFolderOf(commandLine));

    [Fact]
    public async Task A_daemon_running_from_a_folder_a_reinstall_deleted_is_closed_moved_restarted_and_its_missing_torrent_added_back()
    {
        // The reinstall removed the folder; the new data-root folder holds only a rebuilt default.
        Directory.CreateDirectory(Install);
        Directory.CreateDirectory(Path.Combine(Data, "components", "transmission"));
        File.WriteAllText(Path.Combine(Data, "components", "transmission", "settings.json"), "rebuilt");
        var (daemon, rpc, client) = Setup();
        daemon.OnClosing = () => File.WriteAllText(Path.Combine(OldFolder, "settings.json"), "written on close");

        var summary = await RunningDaemonMigration.RunAsync(Install, Data, daemon, client, null, CancellationToken.None);

        Assert.True(rpc.Closed);
        Assert.False(daemon.Killed);
        Assert.True(daemon.Started);
        Assert.False(Directory.Exists(OldFolder));
        Assert.Equal("written on close", File.ReadAllText(Path.Combine(Data, "components", "transmission", "settings.json")));
        Assert.Equal(["magnet:?xt=urn:btih:bbb|/d/TV"], rpc.Added);
        Assert.Contains("added back 1 torrent", summary);
        var notice = TransmissionReadd.Pending(Data)!;
        Assert.Equal(summary, notice.Summary);
        Assert.Empty(notice.Torrents);
    }

    [Fact]
    public async Task When_every_torrent_reloads_nothing_is_added_and_the_notice_only_says_what_happened()
    {
        var (daemon, rpc, client) = Setup(reloadsEverything: true);

        var summary = await RunningDaemonMigration.RunAsync(Install, Data, daemon, client, null, CancellationToken.None);

        Assert.Empty(rpc.Added);
        Assert.Contains("all 2 torrents came back", summary);
    }

    [Fact]
    public async Task A_daemon_that_will_not_close_is_ended_before_its_folder_is_moved()
    {
        var (daemon, _, client) = Setup(reloadsEverything: true);
        daemon.ExitsByItself = false;

        await RunningDaemonMigration.RunAsync(Install, Data, daemon, client, null, CancellationToken.None);

        Assert.True(daemon.Killed);
        Assert.True(daemon.Started);
    }

    [Fact]
    public async Task A_daemon_already_running_from_the_data_folder_is_left_alone()
    {
        var (daemon, rpc, client) = Setup();
        daemon.CommandLine = $"transmission-daemon.exe -f -g \"{Path.Combine(Data, "components", "transmission")}\"";

        Assert.Null(await RunningDaemonMigration.RunAsync(Install, Data, daemon, client, null, CancellationToken.None));
        Assert.False(rpc.Closed);
        Assert.False(daemon.Started);
        Assert.Null(TransmissionReadd.Pending(Data));
    }

    [Fact]
    public async Task No_running_daemon_means_nothing_to_do()
    {
        var (daemon, _, client) = Setup();
        daemon.CommandLine = "";

        Assert.Null(await RunningDaemonMigration.RunAsync(Install, Data, daemon, client, null, CancellationToken.None));
    }
}
