namespace Deluno.Contracts;

public static class MovieCollectionJobTypes
{
    /// <summary>
    /// Collection refreshes have their own executor lane so a metadata refresh
    /// can never queue behind a library search. They still use the existing
    /// heartbeat planner and job queue; this is not a second scheduler.
    /// </summary>
    public const string Sync = "movies.collection.sync";
}
