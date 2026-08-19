namespace Deluno.Jobs.Contracts;

/// <summary>
/// The minimum a catalogue entry needs to expose for the metadata backfill to
/// queue a refresh for it. Deliberately not the full list row: the planner
/// selects these by staleness straight from SQL, and pulling whole entities
/// (overview, metadata blob and all) to build a job payload was the shape that
/// made a 20,000-item backfill load the entire catalogue every pass.
/// </summary>
public sealed record MetadataRefreshCandidate(
    string Id,
    string Title,
    int? Year);
