namespace Deluno.Libraries.Contracts;

/// <summary>
/// How long a processor may go without any change before Deluno stops waiting
/// for it (#511). Stored in minutes on the library as
/// <c>ProcessorTimeoutMinutes</c>.
///
/// <para>Not a limit on how long processing may take. The clock restarts
/// whenever the processor reports a change, pauses while it cannot be reached,
/// and does not run while a scheduled run is still ahead. The owner chose the
/// range: a day at the least, a week at the most, two days unless told
/// otherwise.</para>
/// </summary>
public static class ProcessorStallLimits
{
    public const int MinimumMinutes = 24 * 60;
    public const int DefaultMinutes = 48 * 60;
    public const int MaximumMinutes = 7 * 24 * 60;

    public static int Normalize(int? minutes)
        => minutes is null or <= 0 ? DefaultMinutes : Math.Clamp(minutes.Value, MinimumMinutes, MaximumMinutes);
}
