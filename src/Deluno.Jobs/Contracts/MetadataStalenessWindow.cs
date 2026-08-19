namespace Deluno.Jobs.Contracts;

/// <summary>
/// When a catalogue entry's metadata counts as stale, and how long to leave an
/// entry alone after an attempt.
///
/// Shared because two callers ask the same question and their answers have to
/// agree: the worker's backfill planner decides what to queue, and the manual
/// "refresh metadata" endpoint reports how much is left to do. If the endpoint
/// used a different window it would tell the user a number the planner was
/// never going to act on.
/// </summary>
public static class MetadataStalenessWindow
{
    /// <summary>
    /// How old a successful refresh has to be before the entry is considered
    /// worth revisiting.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    /// <summary>
    /// How long to leave an entry alone after a metadata attempt, whether or
    /// not the provider matched it. Without this an unmatchable title stays
    /// permanently stale and is re-queued on every pass — a hot loop against
    /// the provider, which at 20,000 items and a 1% unmatchable rate would be
    /// 200 pointless lookups a minute, forever.
    /// </summary>
    public static readonly TimeSpan AttemptCooldown = TimeSpan.FromHours(24);
}
