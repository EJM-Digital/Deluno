using System.Net.Sockets;
using Deluno.Contracts.Connectivity;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Deluno.Platform.Connectivity;

/// <summary>
/// The one place that answers "is the internet down" (#901).
///
/// <para>Judged the same way <see cref="Deluno.Filesystem.LibraryAvailabilityService"/>
/// judges a library root: a real probe, cached briefly so nothing hammers the
/// network on every caller, announced to Activity once on the way down and
/// once on the way back up rather than on every tick. The difference is what
/// is probed - independent public hosts rather than one path - and what
/// "down" means: every host has to fail together for this to call it an
/// outage, because one host having a bad day is not the internet being
/// down.</para>
///
/// <para>Deliberately not built from a download's own stall: that was the
/// mistake #901 exists to fix. A stalled torrent and an internet outage look
/// identical from inside the download; only an independent check can tell
/// them apart.</para>
/// </summary>
public sealed class ConnectivityMonitor(
    IConnectivityOutageRepository outageRepository,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<ConnectivityMonitor> logger) : IConnectivityMonitor
{
    /// <summary>
    /// Used when nothing configures <c>Deluno:Connectivity:CheckHosts</c>.
    /// A mix of well-known public resolvers on different networks, so a
    /// single provider's outage is not mistaken for the internet being down.
    /// </summary>
    private static readonly string[] DefaultHosts =
    [
        "1.1.1.1:443",
        "8.8.8.8:443",
        "9.9.9.9:443",
        "one.one.one.one:443"
    ];

    /// <summary>How long an answer is reused before probing again.</summary>
    private static readonly TimeSpan AnswerHoldsFor = TimeSpan.FromSeconds(20);

    /// <summary>How long Deluno waits for one host to answer.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _windowsGate = new(1, 1);
    private ConnectivityStatus? _cached;
    private IReadOnlyList<ConnectivityOutageWindow>? _cachedWindows;
    private DateTimeOffset _cachedWindowsUtc;

    public ConnectivityStatus Current => _cached ?? new ConnectivityStatus(
        IsOutage: false,
        SinceUtc: null,
        CheckedUtc: DateTimeOffset.MinValue,
        CheckedHosts: [],
        Message: "Not checked yet.");

    public async Task<ConnectivityStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && now - _cached.CheckedUtc < AnswerHoldsFor)
            {
                return _cached;
            }

            var hosts = ResolveHosts();
            var reachable = await AnyHostReachableAsync(hosts, cancellationToken);
            // On this instance's very first check there is nothing cached in
            // memory yet - which is not the same as "no outage was ever open".
            // A process restart mid-outage must still find its own window and
            // close it, so the baseline comes from persisted state rather than
            // defaulting to "was fine".
            var wasOutage = _cached?.IsOutage ?? (await ListWindowsCachedAsync(cancellationToken)) is [.., { EndUtc: null }];
            var isOutage = !reachable;

            DateTimeOffset? sinceUtc = isOutage ? _cached?.SinceUtc ?? now : null;

            if (isOutage && !wasOutage)
            {
                sinceUtc = now;
                await outageRepository.RecordOutageStartedAsync(now, cancellationToken);
                _cachedWindows = null;
                await activityFeedRepository.RecordActivityAsync(
                    "connectivity.outage.started",
                    $"Internet down since {FormatClock(now)} — cleanup and searching paused until it's back. Downloads already running keep going in the client.",
                    null,
                    null,
                    "connectivity",
                    null,
                    cancellationToken);
            }
            else if (!isOutage && wasOutage)
            {
                await outageRepository.RecordOutageEndedAsync(now, cancellationToken);
                _cachedWindows = null;
                await activityFeedRepository.RecordActivityAsync(
                    "connectivity.outage.resolved",
                    "Internet connection is back. Deluno has resumed cleanup and searching.",
                    null,
                    null,
                    "connectivity",
                    null,
                    cancellationToken);
            }

            var status = new ConnectivityStatus(
                IsOutage: isOutage,
                SinceUtc: sinceUtc,
                CheckedUtc: now,
                CheckedHosts: hosts,
                Message: isOutage
                    ? $"Internet down since {FormatClock(sinceUtc ?? now)} — cleanup and searching paused."
                    : "Connected.");
            _cached = status;
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TimeSpan> OutageOverlapAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        if (until <= since)
        {
            return TimeSpan.Zero;
        }

        var windows = await ListWindowsCachedAsync(cancellationToken);
        var overlap = TimeSpan.Zero;
        foreach (var window in windows)
        {
            var start = window.StartUtc > since ? window.StartUtc : since;
            var windowEnd = window.EndUtc ?? until;
            var end = windowEnd < until ? windowEnd : until;
            if (end > start)
            {
                overlap += end - start;
            }
        }

        return overlap;
    }

    /// <summary>
    /// Outage windows change at most twice per outage (open, then close), so
    /// this is read once and reused for <see cref="AnswerHoldsFor"/> rather
    /// than hitting the database on every caller of <see cref="OutageOverlapAsync"/>
    /// - a stall check runs against every queue item on every telemetry pass.
    /// </summary>
    private async Task<IReadOnlyList<ConnectivityOutageWindow>> ListWindowsCachedAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await _windowsGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedWindows is not null && now - _cachedWindowsUtc < AnswerHoldsFor)
            {
                return _cachedWindows;
            }

            var windows = await outageRepository.ListWindowsAsync(cancellationToken);
            _cachedWindows = windows;
            _cachedWindowsUtc = now;
            return windows;
        }
        finally
        {
            _windowsGate.Release();
        }
    }

    private string[] ResolveHosts()
    {
        var configured = configuration.GetValue<string?>("Deluno:Connectivity:CheckHosts");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultHosts;
        }

        var hosts = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return hosts.Length > 0 ? hosts : DefaultHosts;
    }

    /// <summary>
    /// One host answering is enough to say the internet is up; every host
    /// failing together is what makes this an outage rather than one
    /// provider's bad day. Probed in parallel so one slow host does not delay
    /// the others past <see cref="ProbeTimeout"/>.
    /// </summary>
    private async Task<bool> AnyHostReachableAsync(IReadOnlyList<string> hosts, CancellationToken cancellationToken)
    {
        var probes = hosts.Select(host => ProbeAsync(host, cancellationToken)).ToArray();
        var results = await Task.WhenAll(probes);
        return results.Any(reachable => reachable);
    }

    private async Task<bool> ProbeAsync(string hostAndPort, CancellationToken cancellationToken)
    {
        var (host, port) = ParseHostAndPort(hostAndPort);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port, timeout.Token);
            await connect.AsTask().WaitAsync(ProbeTimeout, timeProvider, timeout.Token);
            return client.Connected;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Connectivity probe to {Host}:{Port} failed.", host, port);
            return false;
        }
    }

    private static (string Host, int Port) ParseHostAndPort(string hostAndPort)
    {
        var separator = hostAndPort.LastIndexOf(':');
        if (separator > 0 && int.TryParse(hostAndPort[(separator + 1)..], out var port))
        {
            return (hostAndPort[..separator], port);
        }

        return (hostAndPort, 443);
    }

    private static string FormatClock(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm");
}
