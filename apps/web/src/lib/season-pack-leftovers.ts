/**
 * Reading a season pack's own leftovers out of the Activity event that
 * recorded its import (#889): the files that never resolved to a canonical
 * episode, or that matched one but had a problem of their own, so a person
 * can see what did not match and delete them from where the import left
 * them.
 *
 * The event's `detailsJson` is written by ImportPipelineService with a
 * `Skipped` array of `{ SourcePath, Reason }`, and a later
 * `filesystem.import.season-pack.leftovers-deleted` event's `detailsJson`
 * carries a `Deleted` array of the paths a person has already confirmed
 * removing. Reading both keeps the problem visible only until it is
 * actually cleared, across a reload, rather than for the life of the
 * browser tab.
 */

export const SEASON_PACK_COMPLETED_CATEGORY = "filesystem.import.season-pack.completed";
export const SEASON_PACK_LEFTOVERS_DELETED_CATEGORY = "filesystem.import.season-pack.leftovers-deleted";

export interface SeasonPackSkippedFile {
  sourcePath: string;
  reason: string;
}

export function parseSeasonPackSkippedFiles(detailsJson: string | null | undefined): SeasonPackSkippedFile[] {
  if (!detailsJson) return [];
  try {
    const parsed = JSON.parse(detailsJson) as Record<string, unknown>;
    const skipped = field(parsed, "skipped");
    if (!Array.isArray(skipped)) return [];
    const files: SeasonPackSkippedFile[] = [];
    for (const entry of skipped) {
      if (typeof entry !== "object" || entry === null) continue;
      const record = entry as Record<string, unknown>;
      const sourcePath = text(field(record, "sourcePath"));
      if (!sourcePath) continue;
      files.push({ sourcePath, reason: text(field(record, "reason")) ?? "Did not resolve to a canonical episode." });
    }
    return files;
  } catch {
    return [];
  }
}

export function parseDeletedLeftoverPaths(detailsJson: string | null | undefined): string[] {
  if (!detailsJson) return [];
  try {
    const parsed = JSON.parse(detailsJson) as Record<string, unknown>;
    const deleted = field(parsed, "deleted");
    if (!Array.isArray(deleted)) return [];
    return deleted.filter((entry): entry is string => typeof entry === "string");
  } catch {
    return [];
  }
}

/** The plain filename off a full path, whichever separator its platform used. */
export function fileNameFromPath(path: string): string {
  const normalized = path.replace(/\\/g, "/");
  const parts = normalized.split("/").filter(Boolean);
  return parts[parts.length - 1] ?? path;
}

function field(source: Record<string, unknown>, name: string): unknown {
  const key = Object.keys(source).find((candidate) => candidate.toLowerCase() === name.toLowerCase());
  return key === undefined ? undefined : source[key];
}

function text(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}
