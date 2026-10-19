using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Notifications;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem;

/// <summary>Why a library is not usable right now, or that it is (#909).</summary>
public enum LibraryRootProblem
{
    None,

    /// <summary>The root could not be read at all.</summary>
    Unreachable,

    /// <summary>The root answers, but Deluno cannot write to it.</summary>
    ReadOnly,

    /// <summary>Free space is at or below the pause threshold.</summary>
    LowFreeSpace
}

/// <summary>
/// One library's root, as of the last check (#909): reachable, writable, how
/// much space is left, and whether any of that blocks grabs and imports.
/// </summary>
public sealed record LibraryRootStatus(
    string LibraryId,
    string LibraryName,
    string RootPath,
    bool Reachable,
    bool? Writable,
    double? FreePercent,
    LibraryRootProblem Problem,
    /// <summary>
    /// True when free space is below the warn threshold but still above the
    /// pause threshold -- worth a problem on Health, but not worth stopping
    /// grabs and imports over yet.
    /// </summary>
    bool WarnLowFreeSpace,
    string Message)
{
    public bool BlocksGrabs => Problem != LibraryRootProblem.None;
}

/// <summary>
/// Which libraries Deluno can act on right now, and which it must leave alone.
/// </summary>
/// <param name="UnreachableLibraryIds">
/// Libraries whose root could not be read. Nothing in them is searched for,
/// imported into, or reported on until the path is back. Kept alongside
/// <see cref="Statuses"/> because it is what existing callers and tests read.
/// </param>
public sealed record LibraryAvailability(
    IReadOnlySet<string> UnreachableLibraryIds,
    IReadOnlyDictionary<string, LibraryRootStatus> Statuses)
{
    /// <summary>
    /// One clear problem, not failures title by title (#909, DESIGN-007
    /// decision 12): unreachable, read-only, or too full to safely keep
    /// writing to all pause the same way, whichever one applies.
    /// </summary>
    public bool IsUsable(string libraryId)
        => !Statuses.TryGetValue(libraryId, out var status) || !status.BlocksGrabs;
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
///
/// <para><b>#909 extends the same gate</b> rather than adding a second one: a
/// full or read-only root pauses a library exactly the way an unreachable one
/// does, with its own reason in the one message, instead of failing title by
/// title while search carries on regardless.</para>
/// </summary>
public sealed class LibraryAvailabilityService(
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    ILogger<LibraryAvailabilityService> logger,
    IPlatformSettingsRepository? platformSettings = null,
    IOutboundNotificationService? notificationService = null) : ILibraryAvailabilityService
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
    private Dictionary<string, LibraryRootProblem> _announced = new(StringComparer.OrdinalIgnoreCase);

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

            var (warnPercent, pausePercent) = await ReadThresholdsAsync(cancellationToken);
            var unreachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var statuses = new Dictionary<string, LibraryRootStatus>(StringComparer.OrdinalIgnoreCase);

            foreach (var library in libraries)
            {
                if (string.IsNullOrWhiteSpace(library.RootPath))
                {
                    // A library with no root configured is not an outage; it is
                    // an unfinished setup, and something else says so.
                    continue;
                }

                var status = await CheckRootAsync(library, warnPercent, pausePercent, cancellationToken);
                statuses[library.Id] = status;
                if (status.Problem == LibraryRootProblem.Unreachable)
                {
                    unreachable.Add(library.Id);
                }
            }

            await AnnounceChangesAsync(libraries, statuses, cancellationToken);

            _cached = new LibraryAvailability(unreachable, statuses);
            _cachedUtc = now;
            _announced = statuses.ToDictionary(pair => pair.Key, pair => pair.Value.Problem, StringComparer.OrdinalIgnoreCase);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(int WarnPercent, int PausePercent)> ReadThresholdsAsync(CancellationToken cancellationToken)
    {
        if (platformSettings is null)
        {
            return (10, 5);
        }

        try
        {
            var settings = await platformSettings.GetAsync(cancellationToken);
            return (settings.LibraryFreeSpaceWarnPercent, settings.LibraryFreeSpacePausePercent);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Could not read library free-space thresholds; using the defaults.");
            return (10, 5);
        }
    }

    private async Task<LibraryRootStatus> CheckRootAsync(
        LibraryItem library,
        int warnPercent,
        int pausePercent,
        CancellationToken cancellationToken)
    {
        if (!await ReachableAsync(library.RootPath, cancellationToken))
        {
            return new LibraryRootStatus(
                library.Id, library.Name, library.RootPath,
                Reachable: false, Writable: null, FreePercent: null,
                Problem: LibraryRootProblem.Unreachable, WarnLowFreeSpace: false,
                Message: $"{library.Name} is not reachable at {library.RootPath}.");
        }

        var writable = await WritableAsync(library.RootPath, cancellationToken);
        var freePercent = TryReadFreePercent(library.RootPath);

        if (writable == false)
        {
            return new LibraryRootStatus(
                library.Id, library.Name, library.RootPath,
                Reachable: true, Writable: false, FreePercent: freePercent,
                Problem: LibraryRootProblem.ReadOnly, WarnLowFreeSpace: false,
                Message: $"The {library.Name} library's root at {library.RootPath} answers, but Deluno cannot write to it.");
        }

        if (freePercent is { } percent)
        {
            if (percent <= pausePercent)
            {
                return new LibraryRootStatus(
                    library.Id, library.Name, library.RootPath,
                    Reachable: true, Writable: true, FreePercent: percent,
                    Problem: LibraryRootProblem.LowFreeSpace, WarnLowFreeSpace: false,
                    Message: $"The {library.Name} library's root at {library.RootPath} has only {percent:0.#}% free space left, at or below the {pausePercent}% pause threshold.");
            }

            if (percent <= warnPercent)
            {
                return new LibraryRootStatus(
                    library.Id, library.Name, library.RootPath,
                    Reachable: true, Writable: true, FreePercent: percent,
                    Problem: LibraryRootProblem.None, WarnLowFreeSpace: true,
                    Message: $"The {library.Name} library's root at {library.RootPath} has {percent:0.#}% free space left, at or below the {warnPercent}% warning threshold.");
            }
        }

        return new LibraryRootStatus(
            library.Id, library.Name, library.RootPath,
            Reachable: true, Writable: true, FreePercent: freePercent,
            Problem: LibraryRootProblem.None, WarnLowFreeSpace: false,
            Message: $"The {library.Name} library's root at {library.RootPath} is reachable and writable.");
    }

    /// <summary>
    /// Said once when a library's blocking problem changes -- starts, clears,
    /// or changes kind -- never on every tick. A pause nobody is told about is
    /// indistinguishable from Deluno having quietly stopped working.
    /// </summary>
    private async Task AnnounceChangesAsync(
        IReadOnlyList<LibraryItem> libraries,
        Dictionary<string, LibraryRootStatus> statuses,
        CancellationToken cancellationToken)
    {
        foreach (var library in libraries)
        {
            var was = _announced.GetValueOrDefault(library.Id, LibraryRootProblem.None);
            var status = statuses.GetValueOrDefault(library.Id);
            var now = status?.Problem ?? LibraryRootProblem.None;
            if (was == now)
            {
                continue;
            }

            var isBlocked = now != LibraryRootProblem.None;
            var message = isBlocked
                ? $"{status!.Message} Deluno has paused it: nothing in it will be searched for or imported, and nothing has been changed. It resumes on its own once the check passes."
                : $"{library.Name} is reachable and writable again. Deluno has resumed searching and importing for it.";

            await activityFeedRepository.RecordActivityAsync(
                isBlocked ? "library.paused" : "library.resumed",
                message,
                null,
                null,
                "library",
                library.Id,
                cancellationToken);

            // #909: a paused or resumed library is exactly the kind of thing
            // outbound notifications exist for, and this is the one place that
            // already knows -- once, not on every tick -- that it changed.
            if (notificationService is not null)
            {
                await NotificationOutboundDispatch.GuardAsync(async () => await notificationService.DispatchAsync(
                    isBlocked ? "library.paused" : "library.resumed",
                    isBlocked ? $"{library.Name} paused" : $"{library.Name} resumed",
                    message,
                    null,
                    cancellationToken), "library.paused");
            }
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

    /// <summary>
    /// A full or read-only root fails one title at a time at import, with
    /// nothing saying why (#909). A probe file answers both questions at once
    /// -- full and read-only look identical from here, and both mean the same
    /// thing: stop writing until a person fixes it.
    /// </summary>
    private async Task<bool?> WritableAsync(string rootPath, CancellationToken cancellationToken)
    {
        var probePath = Path.Combine(rootPath, $".deluno-health-{Guid.NewGuid():N}.tmp");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            var probe = Task.Run(() =>
            {
                File.WriteAllBytes(probePath, [0]);
                File.Delete(probePath);
            }, timeout.Token);
            await probe.WaitAsync(ProbeTimeout, timeProvider, timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "{RootPath} is reachable but not writable.", rootPath);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            // Timed out or otherwise inconclusive -- not the same claim as
            // "read-only", so this does not block grabs on its own.
            logger.LogDebug(exception, "Could not tell whether {RootPath} is writable.", rootPath);
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort; a leftover probe file is a papercut, not a problem worth surfacing.
            }
        }
    }

    private double? TryReadFreePercent(string rootPath)
    {
        try
        {
            var drive = new DriveInfo(rootPath);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return null;
            }

            return Math.Round((double)drive.AvailableFreeSpace / drive.TotalSize * 100d, 2);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Could not read free space for {RootPath}.", rootPath);
            return null;
        }
    }
}
