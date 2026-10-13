namespace Deluno.Contracts;

/// <summary>
/// How long Deluno waits for Weir to answer after installing, updating or starting it, and what it says while it waits (#1258).
/// Weir's supervised start can retry with a back-off from 2 s to 60 s, and Weir asks callers to allow about three minutes.
/// </summary>
public static class WeirStartWait
{
    /// <summary>The seconds Weir is given to answer, as the bundled-components file states them.</summary>
    public const int BudgetSeconds = 180;

    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(BudgetSeconds);

    /// <summary>The plain words shown while the wait lasts.</summary>
    public const string Notice = "Weir is starting; this can take a few minutes after an update.";
}
