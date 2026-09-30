using System.Collections.Concurrent;

namespace Deluno.Infrastructure.Caching;

/// <summary>
/// Keeps the answer to an expensive read for a few seconds, and drops it the
/// moment the data underneath it changes (#1081).
///
/// <para>Two things end an entry. Time: it is never older than the
/// <c>lifetime</c> asked for. And the caller's <c>generation</c>: a number that
/// moves whenever anything is written to the database the answer was read from
/// (<see cref="Storage.IDelunoDatabaseConnectionFactory.GetWriteGeneration"/>).
/// The generation is read <em>before</em> the query runs, so a write that lands
/// while the query is running leaves the stored entry one generation behind and
/// the next caller recomputes; nothing ever serves an answer older than a change
/// it could have seen.</para>
///
/// <para>Callers arriving while the answer is being computed share that one
/// computation instead of each running the query. A computation that throws is
/// not kept.</para>
/// </summary>
public sealed class GenerationMemo<TValue>(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<TValue> GetOrAddAsync(
        string key,
        long generation,
        TimeSpan lifetime,
        Func<Task<TValue>> compute,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        if (_entries.TryGetValue(key, out var existing)
            && existing.Generation == generation
            && existing.ExpiresUtc > now)
        {
            return await existing.Value.WaitAsync(cancellationToken);
        }

        // Started off the caller's thread and token: it is shared, so one
        // caller giving up must not cancel it for the others.
        var created = new Entry(generation, now + lifetime, Task.Run(compute, CancellationToken.None));
        _entries[key] = created;
        try
        {
            return await created.Value.WaitAsync(cancellationToken);
        }
        catch when (created.Value.IsFaulted || created.Value.IsCanceled)
        {
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, created));
            throw;
        }
    }

    public void Clear() => _entries.Clear();

    private sealed record Entry(long Generation, DateTimeOffset ExpiresUtc, Task<TValue> Value);
}
