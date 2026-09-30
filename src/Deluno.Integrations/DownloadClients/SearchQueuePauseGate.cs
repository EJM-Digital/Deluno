using System.Collections.Concurrent;
using Deluno.Platform.Data;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// #1002: an optional pause of the automatic search cycles while more than N
/// of Deluno's own downloads are still waiting or downloading. It reads the
/// same normalized queue Transfers shows (Deluno's own downloads only, #933),
/// per media type, so a long film queue never holds back TV. Off for every new
/// and existing install (#936). A search a person starts is never asked.
/// </summary>
public interface ISearchQueuePauseGate
{
    Task<SearchQueuePauseDecision> EvaluateAsync(string mediaType, CancellationToken cancellationToken);

    /// <summary>
    /// The one Activity line to write for a change of state (paused, running
    /// again, queue unreadable), or null while nothing changed, so a pause is
    /// announced once and not on every cycle.
    /// </summary>
    SearchQueuePauseAnnouncement? TakeAnnouncement(string mediaType, SearchQueuePauseDecision decision);
}

public sealed record SearchQueuePauseDecision(
    bool Enabled,
    bool Paused,
    bool Unreadable,
    int Waiting,
    int Threshold,
    string? UnreadableReason)
{
    public static SearchQueuePauseDecision Off { get; } = new(false, false, false, 0, 0, null);
}

public sealed record SearchQueuePauseAnnouncement(string Kind);

public static class SearchQueuePauseAnnouncementKinds
{
    public const string Paused = "paused";
    public const string Resumed = "resumed";
    public const string Unreadable = "unreadable";
}

/// <summary>What was last announced per media type; a singleton, because the gate itself is scoped per job.</summary>
public sealed class SearchQueuePauseMemory
{
    internal ConcurrentDictionary<string, string> Announced { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SearchQueuePauseGate(
    IPlatformSettingsRepository settingsRepository,
    IDownloadClientTelemetryService telemetry,
    SearchQueuePauseMemory memory) : ISearchQueuePauseGate
{
    private const string Clear = "clear";

    // one extra line is written, which is the honest thing to say anyway.

    public async Task<SearchQueuePauseDecision> EvaluateAsync(string mediaType, CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetAsync(cancellationToken);
        if (!settings.PauseSearchWhenQueueLongEnabled)
        {
            return SearchQueuePauseDecision.Off;
        }

        var threshold = Math.Max(1, settings.PauseSearchWhenQueueLongThreshold);
        try
        {
            var overview = await telemetry.GetLatestOverviewAsync(DownloadTelemetryFreshness.Acting, cancellationToken);
            return Decide(threshold, mediaType, overview);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SearchQueuePauseDecision(true, false, true, 0, threshold, exception.Message);
        }
    }

    /// <summary>
    /// The decision for a queue reading Deluno already holds, with no client
    /// call. <see cref="EvaluateAsync"/> uses it after reading the queue; the
    /// Dashboard uses it on the last snapshot so showing the gate never asks a
    /// client anything.
    /// </summary>
    public static SearchQueuePauseDecision Decide(int threshold, string mediaType, DownloadTelemetryOverview overview)
    {
        var broken = overview.Clients.FirstOrDefault(client => client.LastFailure is not null);
        if (broken is not null)
        {
            return new SearchQueuePauseDecision(true, false, true, 0, threshold, $"{broken.ClientName} did not answer");
        }

        var waiting = overview.Clients
            .SelectMany(client => client.Queue)
            .Count(item => IsWaiting(item) && CountsFor(item.MediaType, mediaType));
        return new SearchQueuePauseDecision(true, waiting > threshold, false, waiting, threshold, null);
    }

    public SearchQueuePauseAnnouncement? TakeAnnouncement(string mediaType, SearchQueuePauseDecision decision)
    {
        var current = !decision.Enabled ? Clear
            : decision.Unreadable ? SearchQueuePauseAnnouncementKinds.Unreadable
            : decision.Paused ? SearchQueuePauseAnnouncementKinds.Paused
            : Clear;
        var previous = memory.Announced.TryGetValue(mediaType, out var last) ? last : Clear;
        if (previous == current)
        {
            return null;
        }

        if (current == Clear)
        {
            memory.Announced.TryRemove(mediaType, out _);
            return previous == SearchQueuePauseAnnouncementKinds.Paused
                ? new SearchQueuePauseAnnouncement(SearchQueuePauseAnnouncementKinds.Resumed)
                : null;
        }

        memory.Announced[mediaType] = current;
        return new SearchQueuePauseAnnouncement(current);
    }

    /// <summary>Still downloading or waiting: not finished, not already imported.</summary>
    private static bool IsWaiting(DownloadQueueItem item)
        => !string.Equals(item.Status, DownloadQueueStatuses.Completed, StringComparison.OrdinalIgnoreCase)
           && !string.Equals(item.Status, DownloadQueueStatuses.Imported, StringComparison.OrdinalIgnoreCase);

    private static bool CountsFor(string? itemMediaType, string wanted)
        => string.IsNullOrWhiteSpace(itemMediaType) || IsMovie(itemMediaType) == IsMovie(wanted);

    private static bool IsMovie(string mediaType) => mediaType.StartsWith("movie", StringComparison.OrdinalIgnoreCase);

    public static string Kind(string mediaType) => IsMovie(mediaType) ? "film" : "TV";

    public static string PausedMessage(string mediaType, SearchQueuePauseDecision decision)
        => $"Automatic {Kind(mediaType)} searching is paused: {decision.Waiting} downloads are waiting and you asked for a pause above {decision.Threshold}. It restarts by itself when the queue shrinks. Searches you start yourself still run.";

    public static string ResumedMessage(string mediaType)
        => $"Automatic {Kind(mediaType)} searching is running again: the download queue is back under your limit.";

    public static string UnreadableMessage(string mediaType, SearchQueuePauseDecision decision)
        => $"Deluno could not read the download queue ({decision.UnreadableReason}), so automatic {Kind(mediaType)} searching carries on without the pause.";
}
