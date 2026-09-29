namespace Deluno.Contracts;

/// <summary>What Force Import decided for one blocked import.</summary>
/// <param name="Force">Whether to import past the reason this time.</param>
/// <param name="Why">The plain-words reason it did not, empty when it did.</param>
public sealed record ForceImportDecision(bool Force, string Why)
{
    public static ForceImportDecision Yes { get; } = new(true, string.Empty);

    public static ForceImportDecision No(string why) => new(false, why);
}

/// <summary>
/// Force Import (#1013): importing past a short, named list of minor,
/// known-safe reasons, instead of leaving the download blocked.
///
/// <para><b>Media integrity comes first, so the list is closed.</b> A reason is
/// only ever on it when Deluno's own check could not reach a verdict and found
/// nothing wrong: the file was not judged bad, it simply could not be read at
/// that moment. Anything where Deluno <i>did</i> reach a verdict (a sample, no
/// video, a failed playability check, a title it cannot identify, a file that
/// would replace a better one, a destination that already exists, no free
/// space) is never on it, and no setting, retry count or caller can put it
/// there: <see cref="IsSafeToForce"/> is the only door, and it is asserted
/// against every known reason.</para>
///
/// <para>Cleanuparr's Force Import retries three rejection reasons Sonarr and
/// Radarr raise (two "matched by ID, so automatic import is not possible", one
/// "unable to determine if file is a sample"). Only the last has an equivalent
/// here: Deluno never invents a title or episode identity to get past a
/// mismatch, so the two by-ID reasons are deliberately absent.</para>
/// </summary>
public static class ForceImportPolicy
{
    /// <summary>Forced tries before Deluno stops, when the person has not chosen a number.</summary>
    public const int DefaultMaxTries = 3;

    /// <summary>The most tries anyone can choose.</summary>
    public const int MaxTriesLimit = 25;

    /// <summary>
    /// The only reasons Force Import may go past. Deluno could not read the
    /// file to check it (a share that dropped, a lock, a slow disk); the check
    /// said nothing against it.
    /// </summary>
    public static readonly IReadOnlyList<string> SafeReasons = [ImportFailurePolicy.MediaProbeUnreadable];

    public static bool IsSafeToForce(string? reasonCode)
        => reasonCode is not null
           && SafeReasons.Contains(reasonCode, StringComparer.Ordinal);

    /// <summary>The reason, in words a person reads on the settings screen and in Activity.</summary>
    public static string Describe(string reasonCode) => reasonCode switch
    {
        ImportFailurePolicy.MediaProbeUnreadable =>
            "Deluno could not read the file to check it (a share that dropped, a lock, or a slow disk). Nothing was found wrong with it.",
        _ => "This is not something Force Import goes past."
    };

    public static int ClampMaxTries(int tries) => Math.Clamp(tries, 1, MaxTriesLimit);

    /// <summary>
    /// Whether to force this import now.
    /// </summary>
    /// <param name="timesBlocked">
    /// How many times this download's import has stopped for this reason,
    /// counting the one being decided. The first is always left to the ordinary
    /// retry: a share that dropped for a second should not be forced past.
    /// </param>
    /// <param name="timesForced">Forced tries already made for this download.</param>
    /// <param name="replacesExistingFile">Whether this import would take the place of a file already in the library.</param>
    public static ForceImportDecision Decide(
        string reasonCode,
        bool enabled,
        int timesBlocked,
        int timesForced,
        int maxTries,
        bool replacesExistingFile = false)
    {
        if (!IsSafeToForce(reasonCode))
        {
            return ForceImportDecision.No("This is not on the short list of reasons Force Import goes past.");
        }

        if (!enabled)
        {
            return ForceImportDecision.No("Force Import is off.");
        }

        // A file that was never checked never takes the place of one you
        // already have. Force Import only fills a gap.
        if (replacesExistingFile)
        {
            return ForceImportDecision.No("This import would replace a file you already have, and Force Import never does that.");
        }

        if (timesBlocked < 2)
        {
            return ForceImportDecision.No("The first stop is always left to the ordinary retry.");
        }

        if (timesForced >= ClampMaxTries(maxTries))
        {
            return ForceImportDecision.No("Force Import has used all its tries on this download.");
        }

        return ForceImportDecision.Yes;
    }
}
