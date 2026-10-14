using Microsoft.AspNetCore.SignalR;
using System.Text.RegularExpressions;

namespace Deluno.Realtime.Hubs;

public sealed class ActivityHub(IRealtimeResumeSource resumeSource, RealtimeWatcherTracker? watchers = null) : Hub
{
    private const string SubjectsKey = "Deluno.Realtime.Subjects";
    private static readonly Regex LibrarySubjectPattern = new(
        "^library:[A-Za-z0-9-]{1,64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TitleSubjectPattern = new(
        "^title:[A-Za-z0-9-]{1,64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Adds this connection to the screen subjects it is currently rendering.
    /// Subject names are deliberately constrained because they are client-controlled
    /// routing keys.
    /// </summary>
    public async Task Subscribe(string[] subjects)
    {
        var requestedSubjects = NormalizeAndValidate(subjects);
        await Task.WhenAll(requestedSubjects.Select(subject =>
            Groups.AddToGroupAsync(Context.ConnectionId, subject)));

        var currentSubjects = GetSubjects();
        foreach (var subject in requestedSubjects)
        {
            if (currentSubjects.Add(subject))
            {
                watchers?.Joined(subject);
            }
        }
    }

    /// <summary>Removes this connection from the screen subjects it no longer renders.</summary>
    public async Task Unsubscribe(string[] subjects)
    {
        var requestedSubjects = NormalizeAndValidate(subjects);
        await Task.WhenAll(requestedSubjects.Select(subject =>
            Groups.RemoveFromGroupAsync(Context.ConnectionId, subject)));

        var currentSubjects = GetSubjects();
        foreach (var subject in requestedSubjects)
        {
            if (currentSubjects.Remove(subject))
            {
                watchers?.Left(subject);
            }
        }
    }

    /// <summary>
    /// Called by the client right after connecting or reconnecting with the
    /// last sequence number it saw and the server instance that number came from (#1315). Inside the resume window this replays
    /// what was missed; beyond it, the client is told to resync from REST.
    /// </summary>
    public RealtimeResumeResult Resume(long lastSeq, string[] subjects, string? instanceId)
    {
        _ = NormalizeAndValidate(subjects);
        // A client that names no instance (an older page) cannot prove its sequence is from this run: it resyncs.
        return resumeSource.Resume(lastSeq, GetSubjects(), instanceId ?? string.Empty);
    }

    /// <summary>
    /// A no-op the client invokes on a worker-driven schedule, not a page
    /// timer, so it keeps arriving on time even while the tab sits hidden,
    /// occluded, or minimised (#937) -- Chrome throttles a background page's
    /// own <c>setInterval</c>/<c>setTimeout</c> calls, which is what the
    /// framework's own client keep-alive ping runs on. Any message from the
    /// client resets the server's idle-connection clock, so this invoke
    /// alone is enough to keep the connection alive through throttling;
    /// see <see cref="RealtimeServiceCollectionExtensions"/> for the
    /// matching <c>ClientTimeoutInterval</c>.
    /// </summary>
    public Task Ping() => Task.CompletedTask;

    public override Task OnConnectedAsync()
    {
        watchers?.Connected();
        return base.OnConnectedAsync();
    }

    /// <summary>A dropped connection stops watching everything it had joined.</summary>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        watchers?.Disconnected();
        foreach (var subject in GetSubjects())
        {
            watchers?.Left(subject);
        }

        GetSubjects().Clear();
        return base.OnDisconnectedAsync(exception);
    }

    private HashSet<string> GetSubjects()
    {
        if (Context.Items.TryGetValue(SubjectsKey, out var value) && value is HashSet<string> subjects)
        {
            return subjects;
        }

        var created = new HashSet<string>(StringComparer.Ordinal);
        Context.Items[SubjectsKey] = created;
        return created;
    }

    private static string[] NormalizeAndValidate(string[] subjects)
    {
        var normalized = subjects
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var invalid = normalized.FirstOrDefault(subject => !IsValidSubject(subject));
        if (invalid is not null)
        {
            throw new HubException($"Realtime subject '{invalid}' is not valid.");
        }

        return normalized;
    }

    private static bool IsValidSubject(string subject) =>
        subject is RealtimeGroups.Dashboard or RealtimeGroups.Queue or RealtimeGroups.Activity or RealtimeGroups.Live
        || LibrarySubjectPattern.IsMatch(subject)
        || TitleSubjectPattern.IsMatch(subject);
}

