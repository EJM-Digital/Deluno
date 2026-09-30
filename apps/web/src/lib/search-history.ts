/**
 * Reading a stored search's details (a film's or show's search history row).
 *
 * The releases a search compared are stored under `searchPlan.Candidates` by the
 * automatic search and at the top level by older writers, in either casing, so
 * both detail pages ask here rather than each guessing one shape. A row trimmed
 * by #1083 keeps the first few releases and says how many there were in
 * `CandidateCount`, beside that same list.
 */

type JsonRecord = Record<string, unknown>;

function isRecord(value: unknown): value is JsonRecord {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readKey(record: JsonRecord, key: string): unknown {
  const match = Object.keys(record).find((candidate) => candidate.toLowerCase() === key.toLowerCase());
  return match === undefined ? undefined : record[match];
}

/** The object that holds the candidate list: the document itself or its search plan. */
function findPlan(detailsJson: string | null): JsonRecord | null {
  if (!detailsJson) return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(detailsJson);
  } catch {
    return null;
  }
  if (!isRecord(parsed)) return null;
  if (Array.isArray(readKey(parsed, "candidates"))) return parsed;
  const plan = readKey(parsed, "searchPlan");
  return isRecord(plan) && Array.isArray(readKey(plan, "candidates")) ? plan : null;
}

export function parseSearchCandidates<T>(detailsJson: string | null, normalize: (value: JsonRecord) => T): T[] {
  const plan = findPlan(detailsJson);
  if (!plan) return [];
  return (readKey(plan, "candidates") as unknown[]).filter(isRecord).map(normalize);
}

/** How many releases the search compared, even when only the first few were kept. */
export function searchCandidateCount(detailsJson: string | null): number {
  const plan = findPlan(detailsJson);
  if (!plan) return 0;
  const listed = (readKey(plan, "candidates") as unknown[]).filter(isRecord).length;
  const stored = Number(readKey(plan, "candidateCount"));
  return Number.isFinite(stored) && stored > listed ? stored : listed;
}
