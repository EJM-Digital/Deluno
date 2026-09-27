using Deluno.Contracts;

namespace Deluno.Platform.Contracts;

/// <summary>
/// What Deluno does with the download client's copy once a title is safely in
/// the library (#288).
///
/// A finished download exists twice: in the library, permanently, and in the
/// download client, where it may still be shared with other people. Some sites
/// require that sharing to continue or they penalise the account. Delete too
/// early and the user can lose access; never delete and the drive fills up.
///
/// This is the whole of that decision in one place. It is set globally and
/// overridden per search source, because the requirement comes from the site a
/// release was taken from rather than from the library it landed in.
/// </summary>
public sealed record SharingPolicy(
    string Mode,
    int? ForHours,
    double? UntilRatio,
    string StuckAction,
    int StuckAfterDays,
    /// <summary>
    /// What happens once the sharing target (or the stuck give-up) is
    /// reached (#821): <see cref="CompletionReclaim"/> removes the item and
    /// its data from the download client, the behaviour Deluno has always
    /// had; <see cref="CompletionStop"/> instead just stops seeding — the
    /// client keeps the entry and the files, and nothing about the library
    /// is touched. Global only for now, the same as the rest of this policy
    /// before a source ever overrides it (#820 is the per-rule version of
    /// this choice).
    /// </summary>
    string CompletionAction = SharingPolicy.CompletionReclaim,
    /// <summary>#815: which tracker-privacy downloads this policy covers. "both" (the default) is every existing install's value, so nothing narrows until it is edited.</summary>
    string PrivacyScope = DownloadHealthPrivacyScopes.Both)
{
    /// <summary>Keep sharing until the rule below is met, then reclaim the space.</summary>
    public const string ModeShareThenTidy = "share-then-tidy";

    /// <summary>Reclaim as soon as the import is verified. Fastest, and some sites penalise it.</summary>
    public const string ModeTidyNow = "tidy-now";

    /// <summary>Never touch the download client. The user manages it themselves.</summary>
    public const string ModeLeaveAlone = "leave-alone";

    /// <summary>Stop waiting once <see cref="StuckAfterDays"/> passes, reclaim, and say so.</summary>
    public const string StuckGiveUp = "give-up";

    /// <summary>Keep sharing indefinitely and say so. Never breaks a site's rules; the drive keeps filling.</summary>
    public const string StuckKeepWaiting = "keep-waiting";

    /// <summary>Raise it as something needing a decision rather than acting.</summary>
    public const string StuckAsk = "ask";

    /// <summary>Remove the item and its data from the download client once the target is met (#821). The long-standing behaviour.</summary>
    public const string CompletionReclaim = "reclaim";

    /// <summary>Stop seeding once the target is met, but leave the entry and its files in the download client (#821).</summary>
    public const string CompletionStop = "stop";

    /// <summary>
    /// Safe for someone who reads nothing: keep sharing for three days so no
    /// site is upset, then reclaim automatically, and give up after a fortnight
    /// rather than holding the drive forever.
    /// </summary>
    public static SharingPolicy Default { get; } =
        new(ModeShareThenTidy, ForHours: 72, UntilRatio: null, StuckGiveUp, StuckAfterDays: 14, CompletionReclaim);

    /// <summary>
    /// What "this site is strict" means, in one place.
    ///
    /// A private tracker's own rules are usually some form of "keep sharing for
    /// a long time, give back at least what you took, and do not stop early" —
    /// so that is what this says, rather than a number a beginner would have to
    /// invent. Deluno never gives up on its own here: on a site that polices
    /// hit-and-runs, reclaiming space is not worth an account.
    ///
    /// The web app mirrors these values in <c>STRICT_SHARING</c>
    /// (apps/web/src/routes/connections/forms.ts) the same way it mirrors
    /// <see cref="Default"/> in its settings snapshot. Change one, change both;
    /// both are pinned by tests.
    /// </summary>
    public static SharingPolicy Strict { get; } =
        new(ModeShareThenTidy, ForHours: 336, UntilRatio: 1.0, StuckKeepWaiting, StuckAfterDays: 14, CompletionReclaim);

    public static string NormalizeMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            ModeTidyNow => ModeTidyNow,
            ModeLeaveAlone => ModeLeaveAlone,
            _ => ModeShareThenTidy
        };

    public static string NormalizeStuckAction(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            StuckKeepWaiting => StuckKeepWaiting,
            StuckAsk => StuckAsk,
            _ => StuckGiveUp
        };

    public static string NormalizeCompletionAction(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            CompletionStop => CompletionStop,
            _ => CompletionReclaim
        };

    /// <summary>
    /// The policy this one falls back to for anything it does not state. Used
    /// for a search source's override: every field it leaves unset inherits the
    /// global default, so a site only has to say what makes it different.
    /// </summary>
    public SharingPolicy InheritFrom(SharingPolicy fallback)
        => new(
            string.IsNullOrWhiteSpace(Mode) ? fallback.Mode : NormalizeMode(Mode),
            ForHours ?? fallback.ForHours,
            UntilRatio ?? fallback.UntilRatio,
            string.IsNullOrWhiteSpace(StuckAction) ? fallback.StuckAction : NormalizeStuckAction(StuckAction),
            StuckAfterDays > 0 ? StuckAfterDays : fallback.StuckAfterDays,
            string.IsNullOrWhiteSpace(CompletionAction) ? fallback.CompletionAction : NormalizeCompletionAction(CompletionAction),
            DownloadHealthPrivacyScopes.IsKnown(PrivacyScope) ? PrivacyScope : fallback.PrivacyScope);

    /// <summary>#815: whether this policy's privacy scope covers a download with this reported privacy.</summary>
    public bool AppliesToPrivacy(bool? isPrivate) => DownloadHealthPrivacyScopes.Matches(PrivacyScope, isPrivate);
}
