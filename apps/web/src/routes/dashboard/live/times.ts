/**
 * The times Live says (#1098): when the titles on their way should all be in, how
 * long a station usually takes today, and what is next. All plain functions of the
 * stored lists and the clock, so what is on screen is only ever true.
 */
import type { DownloadDispatchItem, ProcessorHandoffItem, SeriesUpcomingEpisodeItem } from "../../../lib/api";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { formatEta } from "../downloads-kit";
import { buildCountdownRows } from "./countdowns";
import { formatCountdown } from "./countdown";
import type { JourneyCard, Stage } from "./journey";

/** "2:28 pm": the clock time the way Australians read it, the same in every browser. */
export function clockTime(ms: number): string {
  const when = new Date(ms);
  const hours = when.getHours() % 12 || 12;
  return `${hours}:${String(when.getMinutes()).padStart(2, "0")} ${when.getHours() < 12 ? "am" : "pm"}`;
}

/** When every title on its way should be in: the latest finish time the clients' own readings give. Nothing when no reading says. */
export function allInBy(cards: readonly JourneyCard[]): number | undefined {
  let latest: number | undefined;
  for (const card of cards) {
    if (card.stage === "imported" || card.stuck || card.finishAtMs === undefined || !Number.isFinite(card.finishAtMs)) continue;
    if (latest === undefined || card.finishAtMs > latest) latest = card.finishAtMs;
  }
  return latest;
}

/** "8 in progress · all done by about 2:28 pm"; the time is left off when no client gives a reading. */
export function journeyHeading(onTheirWay: number, by: number | undefined): string {
  if (onTheirWay <= 0) return "nothing in progress right now";
  const base = `${onTheirWay.toLocaleString("en-AU")} in progress`;
  return by === undefined ? base : `${base} · all done by about ${clockTime(by)}`;
}

/** The time a download should be done, as the card says it. */
export function doneAbout(finishAtMs: number | undefined): string | null {
  return finishAtMs === undefined || !Number.isFinite(finishAtMs) ? null : `done about ${clockTime(finishAtMs)}`;
}

/** What most journeys through each station took today: needs at least this many finished journeys to say. */
export const MIN_JOURNEYS = 3;

const sameDay = (a: number, b: number) => {
  const first = new Date(a);
  const second = new Date(b);
  return first.getFullYear() === second.getFullYear() && first.getMonth() === second.getMonth() && first.getDate() === second.getDate();
};

/**
 * "Usually N min" for each station, from today's finished journeys: the stored time a sent download was handed to its client,
 * seen there, downloaded and imported, and the time a hand-off spent with the processor. A station with fewer than three
 * finished journeys to average has no reading, and says nothing.
 */
export function usualTimes(dispatches: readonly DownloadDispatchItem[], handoffs: readonly ProcessorHandoffItem[], now: number): Partial<Record<Stage, string>> {
  const samples: Record<"grabbed" | "downloading" | "processor" | "importing", number[]> = { grabbed: [], downloading: [], processor: [], importing: [] };
  const span = (from: string | null | undefined, to: string | null | undefined): number | null => {
    const start = Date.parse(from ?? "");
    const end = Date.parse(to ?? "");
    return Number.isFinite(start) && Number.isFinite(end) && end > start ? end - start : null;
  };
  for (const dispatch of dispatches) {
    const done = Date.parse(dispatch.importCompletedUtc ?? "");
    if (!Number.isFinite(done) || !sameDay(done, now)) continue;
    const waited = span(dispatch.createdUtc, dispatch.detectedUtc);
    if (waited !== null) samples.grabbed.push(waited);
    const downloaded = span(dispatch.detectedUtc, dispatch.importDetectedUtc);
    if (downloaded !== null) samples.downloading.push(downloaded);
    const imported = span(dispatch.importDetectedUtc, dispatch.importCompletedUtc);
    if (imported !== null) samples.importing.push(imported);
  }
  for (const handoff of handoffs) {
    if (handoff.status.toLowerCase() !== "completed") continue;
    const finished = Date.parse(handoff.updatedUtc);
    if (!Number.isFinite(finished) || !sameDay(finished, now)) continue;
    const spent = span(handoff.createdUtc, handoff.updatedUtc);
    if (spent !== null) samples.processor.push(spent);
  }
  const out: Partial<Record<Stage, string>> = {};
  for (const [stage, values] of Object.entries(samples) as Array<[keyof typeof samples, number[]]>) {
    if (values.length < MIN_JOURNEYS) continue;
    const average = values.reduce((sum, value) => sum + value, 0) / values.length / 1000;
    out[stage] = `usually ${formatEta(Math.max(1, Math.round(average)))}`;
  }
  return out;
}

/** One thing coming up: a scheduled job or an episode airing. */
export interface NextItem {
  key: string;
  label: string;
  /** When it happens, in ms; for a job that is running now, now. */
  at: number;
  /** When the job last ran, for the bar that fills towards the next run. */
  last: number | null;
  running: boolean;
  to: string;
  kind: "job" | "airing";
}

const episodeCode = (item: SeriesUpcomingEpisodeItem) => `S${String(item.seasonNumber).padStart(2, "0")}E${String(item.episodeNumber).padStart(2, "0")}`;

/**
 * The scheduled tasks that are something Deluno does for your media: reading the lists you follow and watching the
 * indexers for new releases (the searches of each library are separate rows). Everything else is housekeeping (health
 * checks, backups, cleaning, metadata, reconciling downloads) and is on System's scheduled tasks, never on Live (James,
 * 1 Oct 2026: no overlap between Live and System).
 */
export const MEDIA_TASKS: readonly string[] = ["intake.automation", "rss.sync"];

/** True for a row Next may list: a library's search, or one of the media tasks. */
export function isMediaJob(row: { key: string }): boolean {
  return row.key.startsWith("search:") || (row.key.startsWith("task:") && MEDIA_TASKS.includes(row.key.slice("task:".length)));
}

/** What Deluno will do for your media: the next searches, list checks and episodes airing, in one list, soonest first (a search running now first of all). */
export function buildNext(summary: DashboardSummary | undefined, upcoming: readonly SeriesUpcomingEpisodeItem[], now: number, limit = 6): NextItem[] {
  const items: NextItem[] = [];
  for (const row of buildCountdownRows(summary).filter(isMediaJob)) {
    const at = Date.parse(row.next);
    if (!row.running && !Number.isFinite(at)) continue;
    const last = Date.parse(row.last ?? "");
    items.push({ key: row.key, label: row.label, at: row.running || !Number.isFinite(at) ? now : at, last: Number.isFinite(last) ? last : null, running: row.running, to: row.to, kind: "job" });
  }
  for (const episode of upcoming) {
    const at = Date.parse(episode.airDateUtc);
    if (!Number.isFinite(at) || at < now - 60_000) continue;
    items.push({ key: `airs:${episode.episodeId}`, label: `${episode.title} ${episodeCode(episode)} airs`, at, last: null, running: false, to: `/tv/${episode.seriesId}`, kind: "airing" });
  }
  items.sort((a, b) => Number(b.running) - Number(a.running) || a.at - b.at);
  return items.slice(0, limit);
}

/** A countdown under an hour ("4:12"), a clock time beyond ("3:34 pm"), with the day when it is not today. */
export function whenText(at: number, now: number): string {
  const remaining = at - now;
  if (remaining < 3_600_000) return remaining <= 0 ? "now" : formatCountdown(remaining);
  const startOfDay = (ms: number) => new Date(new Date(ms).getFullYear(), new Date(ms).getMonth(), new Date(ms).getDate()).getTime();
  const days = Math.round((startOfDay(at) - startOfDay(now)) / 86_400_000);
  const clock = clockTime(at);
  if (days <= 0) return clock;
  if (days === 1) return `tomorrow ${clock}`;
  return `${new Date(at).toLocaleDateString("en-AU", { weekday: "short" })} ${clock}`;
}
