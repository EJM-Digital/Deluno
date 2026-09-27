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
  connectivity: "Connectivity"
};

export function categoryLabel(category: string): string {
  const area = category.trim().toLowerCase().split(/[._\s-]+/)[0] ?? "";
  if (CATEGORY_AREAS[area]) return CATEGORY_AREAS[area];
  return area ? area.charAt(0).toUpperCase() + area.slice(1) : "General";
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
