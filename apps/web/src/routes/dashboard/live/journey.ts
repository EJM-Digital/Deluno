/**
 * The journey of every title in flight (#1096): searched, grabbed, downloading,
 * with the processor, importing, in the library. Each stage is worked out from
 * the stored lists the other screens already keep (the download queue, the
 * sent downloads, the processor hand-offs) and the summary; the pushed
 * download readings are laid over the queue so a bar follows the download.
 * Nothing here reaches a client while the screen waits.
 */
import type { DownloadDispatchItem, DownloadQueueItem, ProcessorHandoffItem } from "../../../lib/api";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { isProcessorFailure, isWithProcessor } from "../../../lib/needs-action";
import type { Tone } from "../../../lib/status-tones";
import type { LiveProgress } from "../live-events";

export type Stage = "searching" | "grabbed" | "downloading" | "processor" | "importing" | "imported";

export const STAGES: Stage[] = ["searching", "grabbed", "downloading", "processor", "importing", "imported"];

export interface JourneyCard {
  key: string;
  stage: Stage;
  title: string;
  /** Movie or TV, for the poster and the link. */
  mediaType: "movie" | "tv" | "other";
  entityId?: string | null;
  /** The plain words for where it is right now, with the tone that goes with it. */
  state: { tone: Tone; label: string };
  quality?: string | null;
  sizeBytes?: number | null;
  indexer?: string | null;
  client?: string | null;
  processor?: string | null;
  /** 0 to 100, for a download. */
  progress?: number;
  speedMbps?: number;
  etaSeconds?: number;
  /** When it reached this stage or finished, for "2 min ago". */
  at?: string | null;
  /** The download in its client, when it is one (for its Search again button). */
  queueRef?: { clientId: string; itemId: string };
  /** Where to act on it. */
  to: string;
}

const DOWNLOADING = ["downloading", "stalled", "notTransferring", "paused"];
const IN_PROCESSOR = ["processing", "processed", "waitingForProcessor", "processingFailed"];
const IMPORTING = ["importReady", "importQueued", "completed", "importFailed"];

export function releaseKey(name: string | null | undefined): string {
  return (name ?? "").toLowerCase().replace(/[^a-z0-9]+/g, "");
}

/** "Sintel.2010.1080p.BluRay" reads "Sintel (2010)"; a name with no year is left as it is. */
export function titleFromRelease(name: string): string {
  const match = /^(.*?)[.\s_(-]+((?:19|20)\d{2})(?![0-9])/.exec(name);
  if (!match) return name.replace(/[._]+/g, " ").trim();
  const words = match[1].replace(/[._]+/g, " ").trim();
  return words ? `${words} (${match[2]})` : name;
}

function mediaKind(value: string | null | undefined): "movie" | "tv" | "other" {
  const text = (value ?? "").toLowerCase();
  if (text.includes("movie")) return "movie";
  if (text.includes("series") || text.includes("show") || text.includes("tv") || text.includes("episode")) return "tv";
  return "other";
}

/** The title's own page when we know which title it is, otherwise Transfers. */
export function cardLink(mediaType: JourneyCard["mediaType"], entityId: string | null | undefined): string {
  if (!entityId) return "/queue";
  if (mediaType === "movie") return `/movies/${entityId}`;
  if (mediaType === "tv") return `/tv/${entityId}`;
  return "/queue";
}

export interface JourneyInput {
  queue: DownloadQueueItem[];
  handoffs: ProcessorHandoffItem[];
  dispatches: DownloadDispatchItem[];
  live: ReadonlyMap<string, LiveProgress>;
  summary: DashboardSummary | undefined;
  now: number;
  /** What the clients last reported, so a pushed reading is only trusted over a stored one when newer. */
  queueCapturedUtc?: string;
}

function sameLocalDay(a: number, b: number): boolean {
  const first = new Date(a);
  const second = new Date(b);
  return first.getFullYear() === second.getFullYear() && first.getMonth() === second.getMonth() && first.getDate() === second.getDate();
}

/** Every card on the Live board, grouped by the caller with `byStage`. */
export function buildJourney({ queue, handoffs, dispatches, live, summary, now, queueCapturedUtc }: JourneyInput): JourneyCard[] {
  const cards: JourneyCard[] = [];
  const dispatchByRelease = new Map<string, DownloadDispatchItem>();
  for (const dispatch of dispatches) {
    const key = releaseKey(dispatch.releaseName);
    const known = dispatchByRelease.get(key);
    if (!known || Date.parse(dispatch.createdUtc) > Date.parse(known.createdUtc)) dispatchByRelease.set(key, dispatch);
  }
  const handoffByQueue = new Map(handoffs.filter((item) => item.status.toLowerCase() !== "dismissed").map((item) => [item.queueItemId, item]));
  const seenReleases = new Set<string>();
  const storedAt = Date.parse(queueCapturedUtc ?? "");

  for (const raw of queue) {
    const reading = live.get(raw.id);
    const item = reading && reading.at > (Number.isFinite(storedAt) ? storedAt : 0) && DOWNLOADING.includes(reading.status)
      ? { ...raw, progress: reading.progress, speedMbps: reading.speedMbps, status: reading.status }
      : raw;
    const dispatch = dispatchByRelease.get(releaseKey(item.releaseName));
    const handoff = handoffByQueue.get(item.id);
    let stage: Stage | null = null;
    let state: JourneyCard["state"] = { tone: "info", label: "Working" };

    if (item.status === "queued") { stage = "grabbed"; state = { tone: "info", label: "Waiting in the client" }; }
    else if (DOWNLOADING.includes(item.status)) {
      stage = "downloading";
      state = item.status === "stalled" ? { tone: "warn", label: "Stalled" } : item.status === "paused" ? { tone: "idle", label: "Paused" } : item.status === "notTransferring" ? { tone: "info", label: "Checking files" } : { tone: "info", label: "Downloading" };
    } else if (IN_PROCESSOR.includes(item.status) || (handoff && isWithProcessor(handoff.status))) {
      stage = "processor";
      state = handoff ? handoffState(handoff) : item.status === "processingFailed" ? { tone: "bad", label: "Processor failed" } : { tone: "info", label: item.status === "waitingForProcessor" ? "Waiting for the processor" : "Being cleaned" };
    } else if (IMPORTING.includes(item.status)) {
      stage = "importing";
      state = item.status === "importFailed" ? { tone: "bad", label: "Import failed" } : item.status === "importQueued" ? { tone: "info", label: "Import queued" } : { tone: "info", label: "Ready to import" };
    }
    if (!stage) continue;
    seenReleases.add(releaseKey(item.releaseName));
    const mediaType = mediaKind(dispatch?.mediaType ?? item.mediaType);
    cards.push({
      key: `${item.clientId}:${item.id}`,
      queueRef: { clientId: item.clientId, itemId: item.id },
      stage,
      title: item.title || titleFromRelease(item.releaseName),
      mediaType,
      entityId: dispatch?.entityId,
      state,
      quality: dispatch?.decisionQuality ?? null,
      sizeBytes: item.sizeBytes,
      indexer: item.indexerName || dispatch?.indexerName || null,
      client: item.clientName,
      processor: handoff?.processorName ?? null,
      progress: Number.isFinite(item.progress) ? Math.min(100, Math.max(0, item.progress)) : 0,
      speedMbps: item.speedMbps,
      etaSeconds: item.etaSeconds,
      at: item.addedUtc,
      to: stage === "processor" || stage === "importing" ? "/queue" : cardLink(mediaType, dispatch?.entityId)
    });
  }

  // A hand-off whose download has left the client's list is still with the processor, or back from it.
  for (const handoff of handoffs) {
    if (handoff.status.toLowerCase() === "dismissed" || seenReleases.has(releaseKey(handoff.releaseName))) continue;
    const dispatch = dispatchByRelease.get(releaseKey(handoff.releaseName));
    if (dispatch?.importCompletedUtc) continue;
    const updated = Date.parse(handoff.updatedUtc);
    const recent = Number.isFinite(updated) && now - updated < 6 * 3_600_000;
    let stage: Stage | null = null;
    if (isWithProcessor(handoff.status) || (isProcessorFailure(handoff.status) && recent)) stage = "processor";
    else if (handoff.status.toLowerCase() === "completed" && recent) stage = "importing";
    if (!stage) continue;
    seenReleases.add(releaseKey(handoff.releaseName));
    const mediaType = mediaKind(dispatch?.mediaType ?? handoff.mediaType);
    cards.push({
      key: `handoff:${handoff.id}`,
      stage,
      title: titleFromRelease(handoff.releaseName),
      mediaType,
      entityId: dispatch?.entityId,
      state: stage === "importing" ? { tone: "info", label: "Back from the processor" } : handoffState(handoff),
      quality: dispatch?.decisionQuality ?? null,
      indexer: dispatch?.indexerName ?? null,
      client: dispatch?.downloadClientName ?? null,
      processor: handoff.processorName,
      at: handoff.updatedUtc,
      to: "/queue"
    });
  }

  // What was imported today, newest first: the last stop.
  for (const dispatch of dispatches) {
    const done = Date.parse(dispatch.importCompletedUtc ?? "");
    if (!Number.isFinite(done) || !sameLocalDay(done, now)) continue;
    const mediaType = mediaKind(dispatch.mediaType);
    cards.push({
      key: `imported:${dispatch.id}`,
      stage: "imported",
      title: titleFromRelease(dispatch.releaseName),
      mediaType,
      entityId: dispatch.entityId,
      state: { tone: "ok", label: "In the library" },
      quality: dispatch.decisionQuality ?? null,
      indexer: dispatch.indexerName,
      client: dispatch.downloadClientName,
      at: dispatch.importCompletedUtc,
      to: cardLink(mediaType, dispatch.entityId)
    });
  }

  // Searches: running now, or the last one if it was recent.
  const automation = summary?.automation;
  for (const library of automation?.libraries ?? []) {
    if (library.status !== "running") continue;
    cards.push({
      key: `search:${library.libraryId}`,
      stage: "searching",
      title: library.name,
      mediaType: library.mediaType?.toLowerCase().includes("tv") ? "tv" : "movie",
      state: { tone: "info", label: "Searching now" },
      at: null,
      to: "/search-cycles"
    });
  }
  const cycle = automation?.lastSearchCycle;
  if (cycle && !cards.some((card) => card.key === `search:${cycle.libraryId}`)) {
    const finished = Date.parse(cycle.completedUtc ?? cycle.startedUtc);
    if (Number.isFinite(finished) && now - finished < 24 * 3_600_000) {
      cards.push({
        key: `search:last:${cycle.libraryId}`,
        stage: "searching",
        title: cycle.libraryName,
        mediaType: cycle.mediaType?.toLowerCase().includes("tv") ? "tv" : "movie",
        state: cycle.queued > 0 ? { tone: "ok", label: `Grabbed ${cycle.queued}` } : { tone: "idle", label: "Nothing new to grab" },
        quality: `${cycle.planned.toLocaleString("en-AU")} checked, ${cycle.skipped.toLocaleString("en-AU")} passed over`,
        at: cycle.completedUtc ?? cycle.startedUtc,
        to: "/search-cycles"
      });
    }
  }

  return cards;
}

function handoffState(handoff: ProcessorHandoffItem): JourneyCard["state"] {
  const status = handoff.status.toLowerCase();
  if (isProcessorFailure(status) || handoff.failureMessage) return { tone: "bad", label: "Processor failed" };
  if (status === "waiting") return { tone: "info", label: "Waiting for the processor" };
  if (status === "completed") return { tone: "ok", label: "Returned" };
  return { tone: "info", label: "Being cleaned" };
}

export function byStage(cards: JourneyCard[]): Record<Stage, JourneyCard[]> {
  const grouped = Object.fromEntries(STAGES.map((stage) => [stage, [] as JourneyCard[]])) as Record<Stage, JourneyCard[]>;
  for (const card of cards) grouped[card.stage].push(card);
  grouped.imported.sort((a, b) => Date.parse(b.at ?? "") - Date.parse(a.at ?? ""));
  grouped.downloading.sort((a, b) => (b.progress ?? 0) - (a.progress ?? 0));
  return grouped;
}

/**
 * The header count for each lane. The summary counts the whole queue while the
 * lists the cards come from can be shorter, so a lane says the larger of the
 * two and the list says "and N more" for the difference.
 */
export function laneCounts(grouped: Record<Stage, JourneyCard[]>, summary: DashboardSummary | undefined): Record<Stage, number> {
  const downloads = summary?.downloads;
  const pick = (cards: JourneyCard[], fromSummary: number | undefined) => Math.max(cards.length, fromSummary ?? 0);
  return {
    searching: grouped.searching.length,
    grabbed: pick(grouped.grabbed, downloads?.queued),
    downloading: pick(grouped.downloading, downloads?.active !== undefined ? downloads.active + (downloads.stalled ?? 0) : undefined),
    processor: pick(grouped.processor, downloads ? downloads.processing + downloads.waitingForProcessor : undefined),
    importing: pick(grouped.importing, downloads?.importReady),
    imported: pick(grouped.imported, summary?.pipeline?.today.imported)
  };
}

/** Which cards changed stage since the last look, so they can be shown arriving. */
export function movedCards(previous: ReadonlyMap<string, Stage>, cards: JourneyCard[]): Set<string> {
  const moved = new Set<string>();
  if (previous.size === 0) return moved;
  for (const card of cards) {
    const before = previous.get(card.key);
    if (before !== undefined && before !== card.stage) moved.add(card.key);
  }
  return moved;
}
