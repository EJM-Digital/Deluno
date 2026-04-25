namespace Deluno.Integrations.Search;

public sealed record MediaSearchPlan(
    MediaSearchCandidate? BestCandidate,
    IReadOnlyList<MediaSearchCandidate> Candidates,
    string Summary);
