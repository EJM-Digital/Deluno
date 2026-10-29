using System.Net;
using System.Text;
using Deluno.Tests.Recorded;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1301: the three real helpers the component installer stands on, with nothing standing in for them: writing a config file only when there is
/// none (<see cref="RealConfigFileWriter"/>, in a real temp folder), starting a program that is not there (<see cref="RealProcessRunner"/>), and asking a client whether it is ready (<see cref="HttpReadinessChecker"/>, answered by the replies Transmission 4.1.3 really sent).
/// Running with Windows' permission prompt (<c>RunElevatedAsync</c>) needs a person to answer the prompt and is proven on the golden VM.
/// </summary>
public sealed class RealComponentInstallHelpersTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "deluno-install-helpers-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    [Fact]
    public async Task A_config_file_is_written_where_there_is_none_with_its_folder_and_an_existing_one_is_left_exactly_as_it_is()
    {
        var writer = new RealConfigFileWriter();
        var path = Path.Combine(folder, "deluge", "web.conf");

        var first = await writer.WriteIfAbsentAsync(path, "{\"port\": 8112}", CancellationToken.None);
        var second = await writer.WriteIfAbsentAsync(path, "{\"port\": 1}", CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal("{\"port\": 8112}", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_config_file_that_cannot_be_written_is_reported_as_not_written_and_not_as_an_error()
    {
        Directory.CreateDirectory(folder);
        var blocker = Path.Combine(folder, "a-file");
        await File.WriteAllTextAsync(blocker, "x");

        var written = await new RealConfigFileWriter().WriteIfAbsentAsync(Path.Combine(blocker, "inside.conf"), "x", CancellationToken.None);

        Assert.False(written);
    }

    [Fact]
    public async Task A_program_that_cannot_start_gives_no_exit_code_and_opens_nothing()
    {
        // Programs that do start are run by RealProcessRunnerTests (a real process that is killed on timeout); a run that would open a console
        // window on this computer's screen is not made here.
        var missing = await new RealProcessRunner().RunAsync(Path.Combine(folder, "no-such-installer.exe"), null, TimeSpan.FromSeconds(5), CancellationToken.None);
        var missingMsi = await new RealProcessRunner().RunAsync(Path.Combine(folder, "no-such-installer.msi"), "/qn", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(missing);
        Assert.Null(missingMsi);
    }

    private sealed class Replay : IDisposable
    {
        private readonly HttpListener listener = new();

        public Replay(Func<HttpListenerRequest, RecordedReply?> answer, int port)
        {
            Address = $"http://127.0.0.1:{port}/transmission/rpc";
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException) { return; }

                    var reply = answer(context.Request);
                    context.Response.StatusCode = reply?.Status ?? 404;
                    var body = Encoding.UTF8.GetBytes(reply?.Body ?? string.Empty);
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            });
        }

        public string Address { get; }

        public void Dispose() => listener.Close();
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task A_client_that_answers_at_all_is_ready_when_no_field_is_asked_and_otherwise_the_field_must_hold_the_expected_value()
    {
        var handshake = RecordedReplies.Transmission("session-handshake").Exchanges[0].Reply;
        var settings = RecordedReplies.Transmission("version").Exchanges[0].Reply;
        var version = settings.Json()["arguments"]!["version"]!.GetValue<string>();
        Assert.Equal(409, handshake.Status);
        using var asking = new Replay(request => request.HttpMethod == "POST" ? settings : handshake, FreePort());
        var checker = new HttpReadinessChecker();

        Assert.True(await checker.CheckAsync(asking.Address, string.Empty, string.Empty, CancellationToken.None));
        using var json = new Replay(_ => settings, FreePort());
        var rightValue = await checker.CheckAsync(json.Address, "result", "success", CancellationToken.None);
        var wrongValue = await checker.CheckAsync(json.Address, "result", "failure", CancellationToken.None);
        var noSuchField = await checker.CheckAsync(json.Address, "nothing-like-this", "x", CancellationToken.None);

        Assert.True(rightValue);
        Assert.False(wrongValue);
        Assert.False(noSuchField);
        Assert.False(string.IsNullOrEmpty(version));
    }

    [Fact]
    public async Task A_client_that_refuses_the_connection_or_answers_an_error_when_a_field_is_wanted_is_not_ready()
    {
        var refusedPort = FreePort();
        var notFound = new Replay(_ => null, FreePort());
        using var _ = notFound;
        var checker = new HttpReadinessChecker();

        Assert.False(await checker.CheckAsync($"http://127.0.0.1:{refusedPort}/transmission/rpc", "result", "success", CancellationToken.None));
        Assert.False(await checker.CheckAsync($"http://127.0.0.1:{refusedPort}/transmission/rpc", string.Empty, string.Empty, CancellationToken.None));
        Assert.False(await checker.CheckAsync(notFound.Address, "result", "success", CancellationToken.None));
        Assert.True(RecordedReplies.Transmission("not-answering").Exchanges[0].Reply.Status == 0);
    }
}
