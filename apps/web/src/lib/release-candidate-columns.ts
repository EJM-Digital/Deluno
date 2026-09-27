/**
 * Sorting and filtering for a search-candidate results table (#753), shared
 * by the movie and show detail pages' interactive search results and by the
 * dedicated multi-indexer Search page — one implementation, so a column
 * added or a sort rule changed shows up identically everywhere a release
 * list is shown, the same reasoning `release-candidate-status.ts` already
 * gives for decision-status colouring.
 */

export interface CandidateColumnsInput {
  indexerName: string;
  indexerProtocol?: string | null;
  releaseAgeHours?: number | null;
  seeders?: number | null;
  leechers?: number | null;
  sizeBytes?: number | null;
}

export type CandidateSortKey = "protocol" | "age" | "seeders" | "leechers" | "size";
export type SortDirection = "asc" | "desc";

/** Newest-first and most-seeded-first read naturally as "desc" defaults; protocol reads naturally as "asc" (alphabetical). */
export function defaultSortDirection(key: CandidateSortKey): SortDirection {
  return key === "protocol" ? "asc" : "desc";
}

function sortValue(candidate: CandidateColumnsInput, key: CandidateSortKey): number | string {
  switch (key) {
    case "protocol":
      return (candidate.indexerProtocol ?? "").toLowerCase();
    case "age":
      // Ties (unknown age) always sort last regardless of direction.
      return candidate.releaseAgeHours ?? Number.POSITIVE_INFINITY;
    case "seeders":
      return candidate.seeders ?? -1;
    case "leechers":
      return candidate.leechers ?? -1;
    case "size":
      return candidate.sizeBytes ?? -1;
  }
}

export function sortCandidates<T extends CandidateColumnsInput>(
  candidates: readonly T[],
  key: CandidateSortKey,
  direction: SortDirection
): T[] {
  const sorted = [...candidates].sort((a, b) => {
    const left = sortValue(a, key);
    const right = sortValue(b, key);
    if (typeof left === "string" || typeof right === "string") {
      return String(left).localeCompare(String(right));
    }
    return left - right;
  });
  return direction === "asc" ? sorted : sorted.reverse();
}

export interface CandidateColumnFilters {
  protocol?: string;
  indexerName?: string;
}

export function filterCandidates<T extends CandidateColumnsInput>(
  candidates: readonly T[],
  filters: CandidateColumnFilters
): T[] {
  return candidates.filter((candidate) => {
    if (filters.protocol && (candidate.indexerProtocol ?? "").toLowerCase() !== filters.protocol.toLowerCase()) {
      return false;
    }
    if (filters.indexerName && candidate.indexerName !== filters.indexerName) {
      return false;
    }
    return true;
  });
}

/** "3h", "2d", or "—" when the age is unknown — the same rough-age reading a release age gets everywhere else in Deluno. */
export function formatReleaseAge(hours?: number | null): string {
  if (hours === null || hours === undefined || Number.isNaN(hours)) return "—";
  if (hours < 1) return "<1h";
  if (hours < 48) return `${Math.round(hours)}h`;
  return `${Math.round(hours / 24)}d`;
}

/** "12 / 3" (seeders / leechers), falling back to "—" for a value the indexer never reported. */
export function formatSeedersAndLeechers(seeders?: number | null, leechers?: number | null): string {
  const seedersLabel = seeders ?? "—";
  const leechersLabel = leechers ?? "—";
  return `${seedersLabel} / ${leechersLabel}`;
}

/** A short, plain-English label for an indexer's protocol code. */
export function formatProtocol(protocol?: string | null): string {
  if (!protocol) return "—";
  switch (protocol.toLowerCase()) {
    case "newznab":
      return "Usenet";
    case "torznab":
    case "tracker":
      return "Torrent";
    case "rss":
      return "RSS";
    default:
      return protocol;
  }
}
