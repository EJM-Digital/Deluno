namespace Deluno.Integrations.Search;

public static class MediaSearchReasons
{
    public const string Ok = "ok";
    public const string NoIndexers = "no_indexers";
    public const string AllIndexersFailed = "all_indexers_failed";
    public const string CircuitOpen = "circuit_open";
    public const string NoResults = "no_results";
    public const string NoUsableRelease = "no_usable_release";
    public const string NotSearchable = "not_searchable";
    public const string LibraryMissing = "library_missing";
}

public sealed record MediaSearchPlan(
    MediaSearchCandidate? BestCandidate,
    IReadOnlyList<MediaSearchCandidate> Candidates,
    string Summary,
    string Reason = MediaSearchReasons.Ok,
    bool CandidatesTruncatedByIndexer = false);
