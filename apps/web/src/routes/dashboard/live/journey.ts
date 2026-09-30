/**
 * The journey of every title in flight (#1096): searched, grabbed, downloading,
 * stuck, with the processor, importing, in the library. Each stage is worked out
 * from the stored lists the other screens already keep (the download queue, the
 * sent downloads, the processor hand-offs) and the summary; the pushed
 * download readings are laid over the queue so a bar follows the download.
 * Nothing here reaches a client while the screen waits.
 *
 * A download that has stopped moving, or that its client reports a problem
 * with, is not "downloading": it is Stuck, in its own lane, with its reason.
 * Which title a card is comes from the sent download it matches (the film, show
 * or episode it was grabbed for); the release name is only its quiet second line.
 */
import type { DownloadDispatchItem, DownloadQueueItem, ProcessorHandoffItem } from "../../../lib/api";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { isProcessorFailure, isWithProcessor, queueItemNeedsAttention } from "../../../lib/needs-action";
import type { Tone } from "../../../lib/status-tones";
import type { LiveProgress, WorkItem } from "../live-events";

export type Stage = "searching" | "grabbed" | "downloading" | "stuck" | "processor" | "importing" | "imported";

export const STAGES: Stage[] = ["searching", "grabbed", "downloading", "stuck", "processor", "importing", "imported"];

/** What a card is about in the library: a film, a show (a season pack or a whole show) or one episode. */
export type EntityKind = "movie" | "series" | "episode";

export interface JourneyCard {
  key: string;
  stage: Stage;
  /** The best name we have before the library is asked: the release name read as a title. */
  title: string;
  /** The release as the indexer named it: a quiet second line and a tooltip. */
  release?: string;
  /** The library title this card is for, from the sent download it matches. */
  entity?: { kind: EntityKind; id: string };
  /** The plain words for where it is right now, with the tone that goes with it. */
  state: { tone: Tone; label: string };
  /** Why it is stuck or has failed, in the words the client or processor gave. */
  reason?: string | null;
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
  /** Search again can act on it: the client says it stopped or failed. */
  canSearchAgain?: boolean;
  /** The processor hand-off, when it failed and a person has to Retry or Give up. */
  failedHandoffId?: string;
  /** Where to act on it when its title is not known. */
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
  // A site's own banner in front of the name ("www Torrenting com ", "[ www.site.org ] - ") is not part of the title.
  const cleaned = name.replace(/^[\s[(]*(?:www[\s._-]+)?[a-z0-9-]+[\s._-]+(?:com|org|net|to|cc|me|info)[\s\])._-]+(?=[A-Za-z0-9])/i, "");
  const match = /^(.*?)[.\s_(-]+((?:19|20)\d{2})(?![0-9])/.exec(cleaned);
  if (!match) return cleaned.replace(/[._]+/g, " ").trim();
  const words = match[1].replace(/[._]+/g, " ").trim();
  return words ? `${words} (${match[2]})` : cleaned;
}

/** "S04E02" (or "Season 4" for a pack) read from a release name, for a show whose episode the library did not name. */
export function episodeFromRelease(name: string): string | null {
  const episode = /\bS(\d{1,2})[\s._-]?E(\d{1,3})\b/i.exec(name);
  if (episode) return `S${episode[1].padStart(2, "0")}E${episode[2].padStart(2, "0")}`;
  const season = /\bS(\d{1,2})\b|\bSeason[\s._-]?(\d{1,2})\b/i.exec(name);
  return season ? `Season ${Number(season[1] ?? season[2])}` : null;
}

function entityOf(dispatch: DownloadDispatchItem | undefined): JourneyCard["entity"] {
  if (!dispatch?.entityId) return undefined;
  const type = (dispatch.entityType ?? "").toLowerCase();
  if (type.includes("episode")) return { kind: "episode", id: dispatch.entityId };
  if (type.includes("movie")) return { kind: "movie", id: dispatch.entityId };
  if (type.includes("series") || type.includes("show")) return { kind: "series", id: dispatch.entityId };
  return undefined;
}

/** The title's own page when we know which title it is, otherwise Transfers. An episode's show is found by the titles lookup. */
export function cardLink(entity: JourneyCard["entity"]): string {
  if (!entity) return "/queue";
  if (entity.kind === "movie") return `/movies/${entity.id}`;
  if (entity.kind === "series") return `/tv/${entity.id}`;
  return "/queue";
}

/**
 * Short names for the route line. Clients and processors are often named after
 * their machine ("Deluge on WIN-7K3"); with one machine in play that suffix says
 * nothing, so it is dropped. With two or more machines it is kept so they can
 * be told apart.
 */
export function shortNamer(names: Array<string | null | undefined>): (name: string | null | undefined) => string | null {
  const hosts = new Set<string>();
  for (const name of names) {
    const host = /\s+on\s+(\S+)$/i.exec(name ?? "")?.[1];
    if (host) hosts.add(host.toLowerCase());
  }
  return (name) => {
    if (!name) return null;
    return hosts.size <= 1 ? name.replace(/\s+on\s+\S+$/i, "").trim() || name : name;
  };
}

/** The route a download took, each stop once: indexer, client, processor. */
export function routeOf(card: Pick<JourneyCard, "indexer" | "client" | "processor">): string {
  const stops: string[] = [];
  for (const stop of [card.indexer, card.client, card.processor]) {
    if (stop && !stops.some((known) => known.toLowerCase() === stop.toLowerCase())) stops.push(stop);
  }
  return stops.join(" → ");
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
  /** A grab on its way to a client or an import under way, from the events that start and end them. */
  work?: WorkItem[];
}

function sameLocalDay(a: number, b: number): boolean {
  const first = new Date(a);
  const second = new Date(b);
  return first.getFullYear() === second.getFullYear() && first.getMonth() === second.getMonth() && first.getDate() === second.getDate();
}

/** Why a download that needs a look needs it, in the words the client gave. */
function stuckReason(item: DownloadQueueItem): string | null {
  const message = item.errorMessage?.trim();
  if (message) return message;
  const finding = item.healthFindings?.find((entry) => entry.summary?.trim());
  if (finding) return finding.summary.trim();
  return item.status === "stalled" ? "It has stopped moving in the download client." : null;
}

/** Every card on the Live board, grouped by the caller with `byStage`. */
export function buildJourney({ queue, handoffs, dispatches, live, summary, now, queueCapturedUtc, work = [] }: JourneyInput): JourneyCard[] {
  const cards: JourneyCard[] = [];
  const dispatchByRelease = new Map<string, DownloadDispatchItem>();
  const dispatchByClientItem = new Map<string, DownloadDispatchItem>();
  for (const dispatch of dispatches) {
    const key = releaseKey(dispatch.releaseName);
    const known = dispatchByRelease.get(key);
    if (!known || Date.parse(dispatch.createdUtc) > Date.parse(known.createdUtc)) dispatchByRelease.set(key, dispatch);
    if (dispatch.torrentHashOrItemId) dispatchByClientItem.set(`${dispatch.downloadClientId}:${dispatch.torrentHashOrItemId}`.toLowerCase(), dispatch);
  }
  const handoffByQueue = new Map(handoffs.filter((item) => item.status.toLowerCase() !== "dismissed").map((item) => [item.queueItemId, item]));
  const seenReleases = new Set<string>();
  const storedAt = Date.parse(queueCapturedUtc ?? "");
  const short = shortNamer([
    ...queue.map((item) => item.clientName),
    ...handoffs.map((item) => item.processorName),
    ...dispatches.map((item) => item.downloadClientName)
  ]);

  for (const raw of queue) {
    const reading = live.get(raw.id);
    const item = reading && reading.at > (Number.isFinite(storedAt) ? storedAt : 0) && DOWNLOADING.includes(reading.status)
      ? { ...raw, progress: reading.progress, speedMbps: reading.speedMbps, status: reading.status }
      : raw;
    const dispatch = dispatchByClientItem.get(`${item.clientId}:${item.id}`.toLowerCase()) ?? dispatchByRelease.get(releaseKey(item.releaseName));
    const handoff = handoffByQueue.get(item.id);
    let stage: Stage | null = null;
    let state: JourneyCard["state"] = { tone: "info", label: "Working" };
    let reason: string | null = null;
    let canSearchAgain = false;

    const beforeTheProcessor = item.status === "queued" || DOWNLOADING.includes(item.status);
    if (beforeTheProcessor && queueItemNeedsAttention(item)) {
      stage = "stuck";
      state = { tone: "bad", label: "Stuck" };
      reason = stuckReason(item);
      canSearchAgain = item.status === "stalled" || Boolean(item.errorMessage);
    } else if (item.status === "queued") {
      stage = "grabbed";
      state = { tone: "info", label: "Waiting in the client" };
    } else if (DOWNLOADING.includes(item.status)) {
      stage = "downloading";
      state = item.status === "paused" ? { tone: "idle", label: "Paused" } : item.status === "notTransferring" ? { tone: "info", label: "Checking files" } : { tone: "info", label: "Downloading" };
    } else if (IN_PROCESSOR.includes(item.status) || (handoff && isWithProcessor(handoff.status))) {
      stage = "processor";
      state = handoff ? handoffState(handoff) : item.status === "processingFailed" ? { tone: "bad", label: "Processor failed" } : { tone: "info", label: item.status === "waitingForProcessor" ? "Waiting for the processor" : "Being cleaned" };
    } else if (IMPORTING.includes(item.status)) {
      stage = "importing";
      state = item.status === "importFailed" ? { tone: "bad", label: "Import failed" } : item.status === "importQueued" ? { tone: "info", label: "Import queued" } : { tone: "info", label: "Ready to import" };
    }
    if (!stage) continue;
    seenReleases.add(releaseKey(item.releaseName));
    const entity = entityOf(dispatch);
    const failedHandoff = stage === "processor" && handoff && (isProcessorFailure(handoff.status) || Boolean(handoff.failureMessage)) ? handoff : undefined;
    if (failedHandoff && !reason) reason = failedHandoff.failureMessage;
    cards.push({
      key: `${item.clientId}:${item.id}`,
      queueRef: { clientId: item.clientId, itemId: item.id },
      stage,
      title: titleFromRelease(item.releaseName || item.title),
      release: item.releaseName || item.title,
      entity,
      state,
      reason,
      canSearchAgain,
      failedHandoffId: failedHandoff?.id,
      quality: dispatch?.decisionQuality ?? null,
      sizeBytes: item.sizeBytes,
      indexer: dispatch?.indexerName || (item.indexerName && item.indexerName !== item.clientName ? item.indexerName : null),
      client: short(item.clientName),
      processor: short(handoff?.processorName),
      progress: Number.isFinite(item.progress) ? Math.min(100, Math.max(0, item.progress)) : 0,
      speedMbps: item.speedMbps,
      etaSeconds: item.etaSeconds,
      at: item.addedUtc,
      to: stage === "processor" || stage === "importing" || stage === "stuck" ? "/queue" : cardLink(entity)
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
    const entity = entityOf(dispatch);
    const failed = stage === "processor" && (isProcessorFailure(handoff.status) || Boolean(handoff.failureMessage));
    cards.push({
      key: `handoff:${handoff.id}`,
      stage,
      title: titleFromRelease(handoff.releaseName),
      release: handoff.releaseName,
      entity,
      state: stage === "importing" ? { tone: "info", label: "Back from the processor" } : handoffState(handoff),
      reason: failed ? handoff.failureMessage : null,
      failedHandoffId: failed ? handoff.id : undefined,
      quality: dispatch?.decisionQuality ?? null,
      indexer: dispatch?.indexerName ?? null,
      client: short(dispatch?.downloadClientName),
      processor: short(handoff.processorName),
      at: handoff.updatedUtc,
      to: "/queue"
    });
  }

  // A grab on its way to a client, or an import under way, that the lists have not caught up with yet.
  for (const item of work) {
    if (!item.release || seenReleases.has(releaseKey(item.release))) continue;
    seenReleases.add(releaseKey(item.release));
    const dispatch = dispatchByRelease.get(releaseKey(item.release));
    const entity = entityOf(dispatch);
    cards.push({
      key: `work:${item.key}`,
      stage: item.kind === "grab" ? "grabbed" : "importing",
      title: titleFromRelease(item.release),
      release: item.release,
      entity,
      state: item.kind === "grab" ? { tone: "info", label: item.client ? `Sending to ${short(item.client)}` : "Sending to the client" } : { tone: "info", label: "Importing now" },
      quality: dispatch?.decisionQuality ?? null,
      indexer: dispatch?.indexerName ?? null,
      client: short(item.client ?? dispatch?.downloadClientName),
      to: item.to
    });
  }

  // What was imported today, newest first: the last stop.
  for (const dispatch of dispatches) {
    const done = Date.parse(dispatch.importCompletedUtc ?? "");
    if (!Number.isFinite(done) || !sameLocalDay(done, now)) continue;
    const entity = entityOf(dispatch);
    cards.push({
      key: `imported:${dispatch.id}`,
      stage: "imported",
      title: titleFromRelease(dispatch.releaseName),
      release: dispatch.releaseName,
      entity,
      state: { tone: "ok", label: "In the library" },
      quality: dispatch.decisionQuality ?? null,
      indexer: dispatch.indexerName,
      client: short(dispatch.downloadClientName),
      at: dispatch.importCompletedUtc,
      to: cardLink(entity)
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

/** Problems first in a lane, so a card that needs a person is never the one left under "and N more". */
const toneRank: Record<Tone, number> = { bad: 0, warn: 1, info: 2, ok: 3, idle: 4 };

export function byStage(cards: JourneyCard[]): Record<Stage, JourneyCard[]> {
  const grouped = Object.fromEntries(STAGES.map((stage) => [stage, [] as JourneyCard[]])) as Record<Stage, JourneyCard[]>;
  for (const card of cards) grouped[card.stage].push(card);
  grouped.imported.sort((a, b) => Date.parse(b.at ?? "") - Date.parse(a.at ?? ""));
  grouped.downloading.sort((a, b) => (b.progress ?? 0) - (a.progress ?? 0));
  for (const stage of ["processor", "importing"] as const) grouped[stage].sort((a, b) => toneRank[a.state.tone] - toneRank[b.state.tone]);
  return grouped;
}

/**
 * The header count for each lane. The summary counts the whole queue while the
 * lists the cards come from can be shorter, so a lane says the larger of the
 * two and the list says "and N more" for the difference. Stuck downloads are not
 * downloading: they have their own count.
 */
export function laneCounts(grouped: Record<Stage, JourneyCard[]>, summary: DashboardSummary | undefined): Record<Stage, number> {
  const downloads = summary?.downloads;
  const pick = (cards: JourneyCard[], fromSummary: number | undefined) => Math.max(cards.length, fromSummary ?? 0);
  return {
    searching: grouped.searching.length,
    grabbed: pick(grouped.grabbed, downloads?.queued),
    downloading: pick(grouped.downloading, downloads?.active),
    stuck: pick(grouped.stuck, downloads?.stalled),
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
