using System.Collections.Concurrent;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// How busy one download client is (#1494): the downloads it is actively running, and the most it will run at once.
/// <see cref="Limit"/> is null where the client has no limit (or has its queue switched off), and such a client is never full.
/// </summary>
public sealed record DownloadClientLoad(int Active, int? Limit)
{
    public bool IsFull => Limit is { } limit && Active >= limit;

    /// <summary>"3 of 3 running", the way the grab's decision words a full client.</summary>
    public string Describe() => $"{Active} of {Limit} running";
}

/// <summary>Reads a client's load for a grab. Null means it could not be read or the protocol has none, and such a client is treated as not full.</summary>
public interface IDownloadClientLoadReader
{
    Task<DownloadClientLoad?> ReadAsync(DownloadClientItem client, CancellationToken cancellationToken);
}

/// <summary>
/// Asks the client itself (its own setting and its own list), bounded to a few seconds and remembered for a short while so a burst of
/// grabs costs one call, not one each (#1494). A failure is remembered too, for a shorter while, so a client that is not answering does
/// not slow every grab.
/// </summary>
public sealed class DownloadClientLoadReader(IDownloadClientRegistry registry, TimeProvider? timeProvider = null) : IDownloadClientLoadReader
{
    internal static readonly TimeSpan ReuseFor = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ReuseFailureFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AskLimit = TimeSpan.FromSeconds(6);
    private static readonly ConcurrentDictionary<string, (DownloadClientLoad? Load, DateTimeOffset At)> Remembered = new(StringComparer.Ordinal);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<DownloadClientLoad?> ReadAsync(DownloadClientItem client, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var key = $"{client.Id}|{client.Host}|{client.Port}|{client.Protocol}";
        if (Remembered.TryGetValue(key, out var held) && now - held.At < (held.Load is null ? ReuseFailureFor : ReuseFor))
        {
            return held.Load;
        }

        if (!registry.TryGet(client.Protocol, out var adapter))
        {
            return null;
        }

        DownloadClientLoad? load = null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(AskLimit);
            load = await adapter.GetLoadAsync(client, limit.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Not answering is not a reason to hold a grab back: the client counts as not full.
        }

        Remembered[key] = (load, now);
        return load;
    }
}
