namespace Deluno.Realtime;

/// <summary>
/// Subject names used to scope realtime events to the screens that consume them.
/// </summary>
public static class RealtimeGroups
{
    public const string Dashboard = "dashboard";
    public const string Queue = "queue";
    public const string Activity = "activity";

    /// <summary>Joined only while Live or Transfers is open: the signal that someone is watching (#1097).</summary>
    public const string Live = Deluno.Contracts.LiveRealtimeSubject.Name;

    /// <summary>One film or show: its page joins this, so it hears only about its own title (#1179).</summary>
    public static string Title(string titleId) => $"title:{titleId}";

    public static string Library(string libraryId) => $"library:{libraryId}";
}
