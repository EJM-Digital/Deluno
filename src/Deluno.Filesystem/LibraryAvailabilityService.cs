using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem;

/// <summary>
/// Which libraries Deluno can act on right now, and which it must leave alone.
/// </summary>
/// <param name="UnreachableLibraryIds">
/// Libraries whose root could not be read. Nothing in them is searched for,
/// imported into, or reported on until the path is back.
/// </param>
public sealed record LibraryAvailability(IReadOnlySet<string> UnreachableLibraryIds)
{
    public bool IsUsable(string libraryId) => !UnreachableLibraryIds.Contains(libraryId);
}

public interface ILibraryAvailabilityService
{
    Task<LibraryAvailability> ReadAsync(IReadOnlyList<LibraryItem> libraries, CancellationToken cancellationToken);
}

/// <summary>
/// The one place that answers "can Deluno act on this library right now".
///
/// <para>DESIGN-007 decision 12. James: <i>"this isnt as bad as you think,
/// thats where other mechanisms come into play with a missing library being
/// flagged as a system health issue which would stop deluno doing anything at
/// a library level"</i> — and then, given the options, <b>"Flag it and pause
/// that library"</b>.</para>
///
/// <para>Flagging was built and pausing was not, so an unmounted drive still
/// had every title in it searched for, every import attempted, and every
/// failure recorded against the release rather than against the drive. One
/// unplugged disk, a thousand failures, one cause.</para>
///
/// <para><b>Shared on purpose.</b> Search planning and import automation both
/// start from the same list of libraries, and both now filter through this. Two
/// implementations of "is it there" would eventually disagree, and the way you
/// would find out is a library that imports but is never searched.</para>
/// </summary>
public sealed class LibraryAvailabilityService(
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    ILogger<LibraryAvailabilityService> logger) : ILibraryAvailabilityService
{
    /// <summary>
    /// How long an answer is reused. The worker asks on every tick, and a
    /// stat call per library per tick against a sleeping NAS is its own
    /// problem.
    /// </summary>
    private static readonly TimeSpan AnswerHoldsFor = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long Deluno waits for a path to answer at all.
    ///
    /// <para>An unreachable network share does not fail quickly — it fails
    /// after the network stack gives up, which can be half a minute. Blocking
    /// the worker for that on every library is worse than the outage, so a
    /// path that will not answer promptly is treated as unreachable, which is
    /// what it is.</para>
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibraryAvailability? _cached;
    private DateTimeOffset _cachedUtc;
    private HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);

    public async Task<LibraryAvailability> ReadAsync(
        IReadOnlyList<LibraryItem> libraries,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && now - _cachedUtc < AnswerHoldsFor)
            {
                return _cached;
            }

            var unreachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries)
            {
                if (string.IsNullOrWhiteSpace(library.RootPath))
                {
                    // A library with no root configured is not an outage; it is
                    // an unfinished setup, and something else says so.
                    continue;
                }

                if (!await ReachableAsync(library.RootPath, cancellationToken))
                {
                    unreachable.Add(library.Id);
                }
            }

            await AnnounceChangesAsync(libraries, unreachable, cancellationToken);

            _cached = new LibraryAvailability(unreachable);
            _cachedUtc = now;
            _announced = unreachable;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Said once when a library goes, and once when it comes back — never on
    /// every tick. A pause nobody is told about is indistinguishable from
    /// Deluno having quietly stopped working.
    /// </summary>
    private async Task AnnounceChangesAsync(
        IReadOnlyList<LibraryItem> libraries,
        HashSet<string> unreachable,
        CancellationToken cancellationToken)
    {
        foreach (var library in libraries)
        {
            var wasUnreachable = _announced.Contains(library.Id);
            var isUnreachable = unreachable.Contains(library.Id);
            if (wasUnreachable == isUnreachable)
            {
                continue;
            }

            await activityFeedRepository.RecordActivityAsync(
                isUnreachable ? "library.paused" : "library.resumed",
                isUnreachable
                    ? $"{library.Name} is not reachable at {library.RootPath}, so Deluno has paused it. Nothing in it will be searched for or imported, and nothing has been changed. It resumes on its own once the path is back."
                    : $"{library.Name} is reachable again. Deluno has resumed searching and importing for it.",
                null,
                null,
                "library",
                library.Id,
                cancellationToken);
        }
    }

    private async Task<bool> ReachableAsync(string rootPath, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            // Off the calling thread, so a share that never answers costs the
            // timeout rather than the worker's tick.
            var probe = Task.Run(() => Directory.Exists(rootPath), timeout.Token);
            return await probe.WaitAsync(ProbeTimeout, timeProvider, timeout.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Could not decide whether {RootPath} is reachable; treating it as not.", rootPath);
            return false;
        }
    }
}
