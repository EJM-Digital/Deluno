using Microsoft.AspNetCore.SignalR;
using System.Text.RegularExpressions;

namespace Deluno.Realtime.Hubs;

public sealed class ActivityHub(IRealtimeResumeSource resumeSource) : Hub
{
    private const string SubjectsKey = "Deluno.Realtime.Subjects";
    private static readonly Regex LibrarySubjectPattern = new(
        "^library:[A-Za-z0-9-]{1,64}$",
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
            currentSubjects.Add(subject);
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
            currentSubjects.Remove(subject);
        }
    }

    /// <summary>
    /// Called by the client right after connecting or reconnecting with the
    /// last sequence number it saw. Inside the resume window this replays
    /// what was missed; beyond it, the client is told to resync from REST.
    /// </summary>
    public RealtimeResumeResult Resume(long lastSeq, string[] subjects)
    {
        _ = NormalizeAndValidate(subjects);
        return resumeSource.Resume(lastSeq, GetSubjects());
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
        subject is RealtimeGroups.Dashboard or RealtimeGroups.Queue or RealtimeGroups.Activity
        || LibrarySubjectPattern.IsMatch(subject);
}

