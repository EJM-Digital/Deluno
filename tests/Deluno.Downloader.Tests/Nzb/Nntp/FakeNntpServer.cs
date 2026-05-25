using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Deluno.Downloader.Tests.Nzb.Nntp;

/// <summary>
/// Loopback NNTP server used by tests. Handles greeting, CAPABILITIES,
/// MODE READER, AUTHINFO USER/PASS, BODY, QUIT, DATE. Bodies are
/// returned with byte-level fidelity (dot-stuffing applied per
/// RFC-3977) so the client's byte-level reader is exercised end-to-end.
/// </summary>
public sealed class FakeNntpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public int Port { get; }

    public List<string> ReceivedCommands { get; } = new();

    public string Greeting { get; set; } = "200 fake nntp ready";
    public bool RequireAuth { get; set; }
    public string? ExpectedUser { get; set; }
    public string? ExpectedPass { get; set; }
    public bool RespondToCapabilities { get; set; } = true;

    /// <summary>Articles by bare message-id (no angle brackets).</summary>
    public Dictionary<string, byte[]> Articles { get; } = new(StringComparer.Ordinal);

    /// <summary>Message-ids the server should reject with 430.</summary>
    public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

    private FakeNntpServer(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public static FakeNntpServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new FakeNntpServer(listener, port);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true))
        {
            await WriteLineAsync(stream, Greeting, ct).ConfigureAwait(false);
            var authed = !RequireAuth;
            var pendingUser = false;

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (line is null) return;

                ReceivedCommands.Add(line);
                var parts = line.Split(' ', 2, StringSplitOptions.None);
                var verb = parts[0].ToUpperInvariant();
                var arg = parts.Length > 1 ? parts[1] : string.Empty;

                switch (verb)
                {
                    case "QUIT":
                        await WriteLineAsync(stream, "205 bye", ct).ConfigureAwait(false);
                        return;

                    case "CAPABILITIES":
                        if (RespondToCapabilities)
                        {
                            await WriteLineAsync(stream, "101 capability list follows", ct).ConfigureAwait(false);
                            await WriteLineAsync(stream, "VERSION 2", ct).ConfigureAwait(false);
                            await WriteLineAsync(stream, "READER", ct).ConfigureAwait(false);
                            await WriteLineAsync(stream, "MODE-READER", ct).ConfigureAwait(false);
                            await WriteLineAsync(stream, "POST", ct).ConfigureAwait(false);
                            if (RequireAuth)
                                await WriteLineAsync(stream, "AUTHINFO USER", ct).ConfigureAwait(false);
                            await WriteLineAsync(stream, ".", ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteLineAsync(stream, "500 unknown command", ct).ConfigureAwait(false);
                        }
                        break;

                    case "MODE":
                        await WriteLineAsync(stream, "200 reader mode ok", ct).ConfigureAwait(false);
                        break;

                    case "DATE":
                        await WriteLineAsync(stream, "111 " + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"), ct).ConfigureAwait(false);
                        break;

                    case "AUTHINFO":
                        var sub = arg.Split(' ', 2);
                        var kind = sub[0].ToUpperInvariant();
                        var val = sub.Length > 1 ? sub[1] : string.Empty;
                        if (kind == "USER")
                        {
                            if (ExpectedUser is null || val == ExpectedUser)
                            {
                                pendingUser = true;
                                await WriteLineAsync(stream, "381 password required", ct).ConfigureAwait(false);
                            }
                            else
                                await WriteLineAsync(stream, "481 bad user", ct).ConfigureAwait(false);
                        }
                        else if (kind == "PASS")
                        {
                            if (pendingUser && (ExpectedPass is null || val == ExpectedPass))
                            {
                                authed = true;
                                pendingUser = false;
                                await WriteLineAsync(stream, "281 ok", ct).ConfigureAwait(false);
                            }
                            else
                                await WriteLineAsync(stream, "481 bad pass", ct).ConfigureAwait(false);
                        }
                        else
                            await WriteLineAsync(stream, "500 unknown authinfo", ct).ConfigureAwait(false);
                        break;

                    case "BODY":
                        if (!authed)
                        {
                            await WriteLineAsync(stream, "480 auth required", ct).ConfigureAwait(false);
                            break;
                        }
                        var id = arg.Trim('<', '>', ' ');
                        if (Missing.Contains(id))
                        {
                            await WriteLineAsync(stream, $"430 no such article {arg}", ct).ConfigureAwait(false);
                            break;
                        }
                        if (!Articles.TryGetValue(id, out var body))
                        {
                            await WriteLineAsync(stream, "423 no article", ct).ConfigureAwait(false);
                            break;
                        }
                        await WriteLineAsync(stream, $"222 0 {arg} body", ct).ConfigureAwait(false);
                        await WriteBodyAsync(stream, body, ct).ConfigureAwait(false);
                        break;

                    default:
                        await WriteLineAsync(stream, "500 unknown command", ct).ConfigureAwait(false);
                        break;
                }
            }
        }
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteBodyAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        using var ms = new MemoryStream(body.Length + 256);
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == 0x0A)
            {
                var lineLen = i - start;
                if (lineLen > 0 && body[start] == (byte)'.') ms.WriteByte((byte)'.');
                ms.Write(body, start, lineLen + 1);
                start = i + 1;
            }
        }
        if (start < body.Length)
        {
            if (body[start] == (byte)'.') ms.WriteByte((byte)'.');
            ms.Write(body, start, body.Length - start);
            ms.WriteByte(0x0D);
            ms.WriteByte(0x0A);
        }
        ms.Write("."u8);
        ms.WriteByte(0x0D);
        ms.WriteByte(0x0A);

        var arr = ms.ToArray();
        await stream.WriteAsync(arr, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); }
        catch { /* shutdown */ }
        _cts.Dispose();
    }
}
