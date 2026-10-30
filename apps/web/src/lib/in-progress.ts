/**
 * What In progress counts as happening now, and what it says about a finished download still in a client (#1448).
 * Plain functions of the lists and the settings, so the page, the sidebar tooltip and the tests read one rule.
 */
import type { DownloadQueueItem } from "./api";
import type { PlatformSettingsSnapshot } from "./api/types/settings";
import { downloadQueueStatuses, isFinishedInClient } from "./download-telemetry";
import { isUsenetProtocol } from "./protocol";

/** The queue split in two: downloading, waiting, with the processor or importing, and finished but still in a client. */
export function splitQueue(queue: readonly DownloadQueueItem[]): { happeningNow: DownloadQueueItem[]; finished: DownloadQueueItem[] } {
  const happeningNow: DownloadQueueItem[] = [];
  const finished: DownloadQueueItem[] = [];
  for (const item of queue) (isFinishedInClient(item.status) ? finished : happeningNow).push(item);
  return { happeningNow, finished };
}

function hoursWords(hours: number): string {
  if (hours >= 48 && hours % 24 === 0) return `${hours / 24} days`;
  return hours === 1 ? "1 hour" : `${hours} hours`;
}

/** The seeding rule's target in words, e.g. "48 hours or a ratio of 2"; null when the rule names none. */
export function seedTargetWords(settings: Pick<PlatformSettingsSnapshot, "sharingForHours" | "sharingUntilRatio">): string | null {
  const parts: string[] = [];
  if (settings.sharingForHours != null && settings.sharingForHours > 0) parts.push(hoursWords(settings.sharingForHours));
  if (settings.sharingUntilRatio != null && settings.sharingUntilRatio > 0) parts.push(`a ratio of ${settings.sharingUntilRatio}`);
  return parts.length ? parts.join(" or ") : null;
}

/**
 * What Deluno will do with one finished download, from the seeding rule already in Settings (Automation & Recovery,
 * Keep Seeding). Nothing here is a new rule.
 */
export function finishedPlan(item: Pick<DownloadQueueItem, "status" | "protocol" | "seedLimit">, settings: Pick<PlatformSettingsSnapshot, "sharingMode" | "sharingForHours" | "sharingUntilRatio" | "sharingCompletionAction">): string {
  if (item.status === downloadQueueStatuses.filesGone) return "The files are gone. Search for another copy, or remove it from the client. Deluno changes nothing here on its own.";
  if (item.status === downloadQueueStatuses.handedOverElsewhere) return "Another copy of this release was handed over, so nothing is owed to this one.";
  const leaves = settings.sharingCompletionAction === "stop" ? "stops seeding it and leaves it in the client" : "removes it from the client";
  const mode = settings.sharingMode;
  if (mode === "leave-alone") return "Deluno leaves it in the client. You decide when to remove it.";
  if (mode === "tidy-now" || isUsenetProtocol(item.protocol)) return "Deluno removes it from the client once the import is checked.";
  const target = seedTargetWords(settings);
  const own = item.seedLimit ? ` The client's own limit: ${item.seedLimit}.` : "";
  return target ? `Seeding until ${target}, then Deluno ${leaves}.${own}` : `Seeding with no target set, so Deluno keeps it in the client.${own}`;
}
