/**
 * Shared formatting for activity-event lists.
 *
 * The live dashboard ticker and the notifications bell (#907) both show the
 * same kind of row -- a message, a category and a fine-grained age -- and
 * each kept its own copy of how to say it. One copy is what keeps "3m ago"
 * meaning the same thing, and coloured the same way, in both places.
 */
import type { DisplayPreferences } from "./display-preferences";
import { formatDateTime, formatShortDate } from "./display-preferences";
import type { LedTone } from "../components/ui/status-led";

export type ActivitySeverity = "info" | "success" | "warning" | "error";

/** A leading light carries the severity, so a bad minute reads as colour first. */
export const SEVERITY_LED: Record<ActivitySeverity, LedTone> = {
  info: "idle",
  success: "ok",
  warning: "warn",
  error: "bad"
};

/** Category slugs come from the backend as machine names; read them out. */
/**
 * The area an event belongs to, in the words a person uses.
 *
 * Categories are internal codes such as `job.failed` or
 * `download.dispatch.sent`; printing them with the punctuation swapped out
 * still read as code ("Job.failed", "Download dispatch.sent"). Only the area
 * is shown: the event's own message already says what happened.
 */
const CATEGORY_AREAS: Record<string, string> = {
  download: "Downloads", dispatch: "Downloads", transfer: "Downloads", release: "Downloads",
  import: "Imports", filesystem: "Imports", storage: "Storage",
  search: "Search", rss: "Search", wanted: "Search",
  job: "Background task", jobs: "Background task",
  library: "Library", media: "Library", episode: "Library",
  metadata: "Metadata", subtitle: "Subtitles", subtitles: "Subtitles",
  retention: "Retention", intake: "Lists", trakt: "Lists", mdblist: "Lists",
  health: "Health", services: "Health", failure: "Health", policy: "Settings",
  processor: "Processing", indexer: "Indexers", notification: "Notifications", system: "System", deluno: "System",
  connectivity: "Connectivity", proxy: "Proxy", remote: "Remote paths"
};

export function categoryLabel(category: string): string {
  const area = category.trim().toLowerCase().split(/[._\s-]+/)[0] ?? "";
  if (CATEGORY_AREAS[area]) return CATEGORY_AREAS[area];
  return area ? area.charAt(0).toUpperCase() + area.slice(1) : "General";
}

/**
 * The event-type filter Activity's history gains in #798, cutting across
 * category prefixes the way Radarr/Sonarr's History filter does — "grabbed",
 * "imported", "failed", "rejected" — rather than one option per raw category
 * slug (there are over ninety of those, which is a dropdown nobody reads).
 */
export type ActivityEventType =
  | "grabbed"
  | "imported"
  | "rejected"
  | "blocklisted"
  | "removed"
  | "failed"
  | "health"
  | "search"
  | "other";

export const ACTIVITY_EVENT_TYPE_LABELS: Record<ActivityEventType, string> = {
  grabbed: "Grabbed",
  imported: "Imported",
  rejected: "Rejected",
  blocklisted: "Blocklisted",
  removed: "Removed",
  failed: "Failed",
  health: "Health",
  search: "Search",
  other: "Other"
};

/**
 * Checked in this order because a category can earn more than one test —
 * `filesystem.import.failed` both names an import and is a failure, and a
 * failure a person can act on is the more useful bucket to find it under.
 */
/**
 * What the bell shows, per the owner's approved proposal on #916: what needs
 * attention or changed the library or queue. A routine background job that
 * found or changed nothing is neither — it is the six near-identical
 * unlabelled lines an hour that used to push a failed grab off the bell's
 * ten.
 */
const BELL_EVENT_TYPES: ReadonlySet<ActivityEventType> = new Set([
  "failed",
  "health",
  "grabbed",
  "imported",
  "removed",
  "blocklisted"
]);

export function mattersToTheBell(event: { category: string; severity?: string }): boolean {
  return BELL_EVENT_TYPES.has(activityEventType(event));
}

/**
 * The three lifecycle events one job run writes — queued, started, completed
 * — folded into the one row that answers "what happened", carrying the
 * others for whoever expands it. #916: a routine recurring job (the media
 * probe every 30 minutes is the one that was actually counted) wrote three
 * near-identical rows per run, which is what buried the events a person
 * needed to see under events nobody was reading.
 *
 * Grouped by `relatedJobId` across the whole list rather than only adjacent
 * rows, the same reason the dashboard ticker's `collapseRepeats` already
 * does that: queued and completed can sit minutes apart with other events
 * between them once more than one job runs at a time.
 *
 * The representative row is whichever of the three is encountered first —
 * in a newest-first list that is `job.completed` once the run has finished
 * (the outcome, which is the one worth reading), or `job.started`/
 * `job.queued` while it is still in flight.
 */
const JOB_LIFECYCLE_CATEGORIES = new Set(["job.queued", "job.started", "job.completed"]);

export interface FoldedActivityEvent<T> {
  event: T;
  /** The other lifecycle events for the same job run, if any were folded into this one. */
  foldedFrom: T[];
}

export function foldRoutineJobEvents<T extends { category: string; relatedJobId: string | null }>(
  events: readonly T[]
): FoldedActivityEvent<T>[] {
  const folded: FoldedActivityEvent<T>[] = [];
  const groups = new Map<string, FoldedActivityEvent<T>>();

  for (const event of events) {
    const jobId = event.relatedJobId;
    if (jobId && JOB_LIFECYCLE_CATEGORIES.has(event.category)) {
      const existing = groups.get(jobId);
      if (existing) {
        existing.foldedFrom.push(event);
        continue;
      }
      const entry: FoldedActivityEvent<T> = { event, foldedFrom: [] };
      groups.set(jobId, entry);
      folded.push(entry);
      continue;
    }
    folded.push({ event, foldedFrom: [] });
  }

  return folded;
}

export function activityEventType(event: { category: string; severity?: string }): ActivityEventType {
  const category = event.category.trim().toLowerCase();

  if (category.includes("block")) return "blocklisted";
  if (category.includes("rejected")) return "rejected";
  if (category.includes("removed") || category.includes("dismissed")) return "removed";
  if (event.severity === "error" || category.includes("failed") || category.includes("failure")) return "failed";
  if (category.includes("import") && !category.includes("failed")) return "imported";
  if (category.includes("dispatch") || category.startsWith("release.") || category.includes("grab")) return "grabbed";
  if (category.startsWith("health")) return "health";
  if (category.startsWith("search") || category.includes(".search.") || category.endsWith(".search")) return "search";
  return "other";
}

/**
 * Finer-grained than `formatShortDate`'s day buckets: a live feed lives and
 * dies by seconds and minutes, which "today"/"yesterday" cannot tell apart.
 */
export function formatEventAge(value: string, preferences: DisplayPreferences): string {
  const then = new Date(value).getTime();
  if (Number.isNaN(then)) return "—";

  if (!preferences.showRelativeDates) return formatDateTime(value, preferences);

  const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));
  if (seconds < 10) return "just now";
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return formatShortDate(value, { ...preferences, showRelativeDates: false });
}
