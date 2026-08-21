namespace Deluno.Realtime;

/// <summary>
/// Subject names used to scope realtime events to the screens that consume them.
/// </summary>
public static class RealtimeGroups
{
    public const string Dashboard = "dashboard";
    public const string Queue = "queue";
    public const string Activity = "activity";

    public static string Library(string libraryId) => $"library:{libraryId}";
}
