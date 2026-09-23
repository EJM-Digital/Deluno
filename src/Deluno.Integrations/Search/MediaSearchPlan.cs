using Deluno.Contracts;

namespace Deluno.Integrations.Search;

public static class MediaSearchReasons
{
    public const string Ok = "ok";
    public const string NoIndexers = "no_indexers";

    /// <summary>The title is in more than one library and the search did not say which (#530).</summary>
    public const string ChooseLibrary = "choose_library";
    public const string AllIndexersFailed = "all_indexers_failed";
    public const string CircuitOpen = "circuit_open";
    public const string NoResults = "no_results";

    /// <summary>Every indexer was skipped for pacing, so none was asked (#598). Not an empty search: it runs again shortly.</summary>
    public const string IndexersBusy = "indexers_busy";
    public const string NoUsableRelease = "no_usable_release";

    /// <summary>
    /// Releases were found and compared, and none was sent because the decision
    /// is a person's.
    ///
    /// <para>This used to be reported as <see cref="NoResults"/>, because the
    /// only thing counted was how many were dispatched. A search that returned
    /// three candidates, ranked them and chose one therefore told the owner
    /// "the linked indexers answered, but none returned a release for this
    /// title" - about a search that had just returned three. Whether anything
    /// was found and whether anything was sent are two questions.</para>
    /// </summary>
    public const string HeldForReview = "held_for_review";
    public const string NotSearchable = "not_searchable";
    public const string LibraryMissing = "library_missing";
    public const string SeasonPackReplacementRequiresEpisodeScope = "season_pack_replacement_requires_episode_scope";
    public const string SeasonPackInstalledEvidenceMissing = "season_pack_installed_evidence_missing";
    public const string SeasonPackCandidateNotUpgradeForEveryEpisode = "season_pack_candidate_not_upgrade_for_every_episode";
}

public sealed record MediaSearchPlan(
    MediaSearchCandidate? BestCandidate,
    IReadOnlyList<MediaSearchCandidate> Candidates,
    string Summary,
    string Reason = MediaSearchReasons.Ok,
    bool CandidatesTruncatedByIndexer = false,
    IReadOnlyList<IntegrationFailure>? Failures = null);
