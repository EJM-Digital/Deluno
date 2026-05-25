using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Deluno.Downloader.Nzb.Nntp;

/// <summary>
/// RFC-3977 NNTP client targeting the subset Usenet binary fetching
/// needs: <c>CAPABILITIES</c>, <c>MODE READER</c>, <c>AUTHINFO USER/PASS</c>,
/// <c>DATE</c> (used as keepalive), <c>BODY &lt;msgid&gt;</c>, <c>QUIT</c>.
///
/// Hardening:
/// <list type="bullet">
///   <item><description>TLS 1.2 / 1.3 only. 0-RTT not enabled (replay
///     risk on AUTHINFO).</description></item>
///   <item><description>Hard-bounded connection age (default 30 min)
///     forces clean reconnect before provider cert rotation can hit a
///     live socket.</description></item>
///   <item><description>Byte-level body reading (see
///     <see cref="ByteLineReader"/>) — yEnc is 8-bit binary.</description></item>
/// </list>
///
/// Not thread-safe — one outstanding command at a time. The pool gives
/// each caller exclusive access.
/// </summary>
public sealed class NntpConnection : IAsyncDisposable
{
    public static readonly TimeSpan DefaultMaxConnectionAge = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultIdleHealthCheckThreshold = TimeSpan.FromSeconds(60);

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly ByteLineReader _reader;
    private readonly DateTimeOffset _establishedAt;
    private DateTimeOffset _lastActivity;
    private bool _disposed;

    public string ServerId { get; }
    public DateTimeOffset EstablishedAt => _establishedAt;
    public DateTimeOffset LastActivity => _lastActivity;
    public TimeSpan Age => DateTimeOffset.UtcNow - _establishedAt;
    public TimeSpan IdleDuration => DateTimeOffset.UtcNow - _lastActivity;

    private NntpConnection(string serverId, TcpClient tcp, Stream stream)
    {
        ServerId = serverId;
        _tcp = tcp;
        _stream = stream;
        _reader = new ByteLineReader(stream);
        _establishedAt = DateTimeOffset.UtcNow;
        _lastActivity = _establishedAt;
    }

    public static async Task<NntpConnection> ConnectAsync(
        NntpServerOptions options, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(options.Host, options.Port, ct).ConfigureAwait(false);

        Stream stream = tcp.GetStream();
        if (options.UseTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                // 0-RTT not enabled by default on Windows; explicit reminder
                // that AUTHINFO must never go in early data.
            }, ct).ConfigureAwait(false);
            stream = ssl;
        }

        var conn = new NntpConnection(options.Id, tcp, stream);

        // Greeting: 200 = posting allowed, 201 = no posting (binaries-only
        // setups). Anything else means the server is unhappy.
        var greeting = await conn.ReadResponseAsync(ct).ConfigureAwait(false);
        if (greeting.Code is not (200 or 201))
            throw new NntpProtocolException($"Unexpected greeting: {greeting}");

        // Probe capabilities. Best-effort; some servers return 500 here.
        var capabilities = await conn.ProbeCapabilitiesAsync(ct).ConfigureAwait(false);

        // INN-style servers require MODE READER to switch from feed mode
        // before they accept BODY commands. Treat it as best-effort.
        if (capabilities.Contains("MODE-READER", StringComparer.OrdinalIgnoreCase) || capabilities.Count == 0)
        {
            try
            {
                var modeReader = await conn.SendAsync("MODE READER", ct).ConfigureAwait(false);
                // Codes 200 / 201 / 502 (not allowed in current state, harmless) all OK to ignore.
                _ = modeReader;
            }
            catch (NntpProtocolException) { /* server rejects; not fatal for binary fetching */ }
        }

        if (!string.IsNullOrEmpty(options.Username))
        {
            await conn.AuthenticateAsync(options.Username!, options.Password ?? string.Empty, ct).ConfigureAwait(false);
        }

        return conn;
    }

    private async Task<IReadOnlyCollection<string>> ProbeCapabilitiesAsync(CancellationToken ct)
    {
        // CAPABILITIES returns a multi-line list. Pre-RFC3977 servers
        // return 5xx; treat that as "no info" and keep going.
        var resp = await SendAsync("CAPABILITIES", ct).ConfigureAwait(false);
        if (resp.Code != 101) return Array.Empty<string>();

        var capabilities = new List<string>();
        var body = await ReadMultilineBodyAsync(ct).ConfigureAwait(false);
        // Body is bytes of ASCII lines.
        var text = Encoding.ASCII.GetString(body);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) capabilities.Add(trimmed);
        }
        return capabilities;
    }

    public async Task AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        var userResp = await SendAsync($"AUTHINFO USER {username}", ct).ConfigureAwait(false);
        if (userResp.Code == 281) return; // accepted on USER alone (rare)
        if (userResp.Code != 381)
            throw new NntpAuthenticationException($"AUTHINFO USER failed: {userResp}");

        var passResp = await SendAsync($"AUTHINFO PASS {password}", ct).ConfigureAwait(false);
        if (passResp.Code != 281)
            throw new NntpAuthenticationException($"AUTHINFO PASS failed: {passResp}");
    }

    /// <summary>Sends DATE as a cheap liveness check on idle borrow.</summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            var resp = await SendAsync("DATE", ct).ConfigureAwait(false);
            return resp.Code == 111;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fetches an article body by Message-ID. Returns the raw yEnc-
    /// encoded body (dot-stuffing removed, terminator stripped).
    /// </summary>
    /// <exception cref="NntpArticleNotFoundException">Server returned 430.</exception>
    public async Task<byte[]> FetchBodyAsync(string messageId, CancellationToken ct)
    {
        var bare = messageId.Trim('<', '>', ' ');
        var wireId = $"<{bare}>";

        var resp = await SendAsync($"BODY {wireId}", ct).ConfigureAwait(false);
        if (resp.Code == 430) throw new NntpArticleNotFoundException(bare);
        if (resp.Code != 222) throw new NntpProtocolException($"BODY failed: {resp}");

        return await ReadMultilineBodyAsync(ct).ConfigureAwait(false);
    }

    public async Task QuitAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        try { await SendAsync("QUIT", ct).ConfigureAwait(false); }
        catch { /* best-effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await QuitAsync().ConfigureAwait(false); } catch { }
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
    }

    private async Task<NntpResponse> SendAsync(string command, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        _lastActivity = DateTimeOffset.UtcNow;
        return await ReadResponseAsync(ct).ConfigureAwait(false);
    }

    private async Task<NntpResponse> ReadResponseAsync(CancellationToken ct)
    {
        var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new NntpProtocolException("Connection closed while reading response.");
        var text = Encoding.ASCII.GetString(line.Span);
        if (text.Length < 3 || !int.TryParse(text.AsSpan(0, 3), out var code))
            throw new NntpProtocolException($"Malformed response: {text}");
        var rest = text.Length > 4 ? text[4..] : string.Empty;
        return new NntpResponse(code, rest);
    }

    private async Task<byte[]> ReadMultilineBodyAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream(capacity: 64 * 1024);
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new NntpProtocolException("Connection closed mid-body.");

            if (line.Length == 1 && line.Span[0] == (byte)'.') break;

            var span = line.Span;
            // Dot-unstuffing: a body line that begins with ".." encodes
            // a single leading "."; strip the extra dot.
            if (span.Length >= 2 && span[0] == (byte)'.' && span[1] == (byte)'.')
                span = span[1..];

            ms.Write(span);
            ms.WriteByte(0x0D);
            ms.WriteByte(0x0A);
        }
        _lastActivity = DateTimeOffset.UtcNow;
        return ms.ToArray();
    }
}
