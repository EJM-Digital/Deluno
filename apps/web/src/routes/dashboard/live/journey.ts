/**
 * The journey of every title in flight (#1098): requested, grabbed, downloading,
 * with the processor, importing, and in the library. Each stage is worked out
 * from the stored lists the other screens already keep (the download queue, the
 * sent downloads, the processor hand-offs) and the summary; the pushed
 * download readings are laid over the queue so a bar follows the download.
 * Nothing here reaches a client while the screen waits.
 *
 * A download that has stopped moving is not a problem card: it stays where it
 * is, amber, saying it is stuck and not moving (a finished download is never stuck), and
 * moves back to Grabbed when another copy is grabbed. Problems a person has to
 * settle are in the bell. Which title a card is comes from the sent download it
 * matches (the film, show or episode it was grabbed for); the release name is
 * only its quiet second line.
 */
import type { DownloadDispatchItem, DownloadQueueItem, ProcessorHandoffItem } from "../../../lib/api";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { isProcessorFailure, isWithProcessor, queueItemNeedsAttention } from "../../../lib/needs-action";
import type { Tone } from "../../../lib/status-tones";
import type { LiveProgress, WorkItem } from "../live-events";
import { plainReason } from "./plain";
import { startedByOfDispatch, type StartedBy } from "./provenance";

export type Stage = "requested" | "grabbed" | "downloading" | "processor" | "importing" | "imported";

export const STAGES: Stage[] = ["requested", "grabbed", "downloading", "processor", "importing", "imported"];

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
  /** Why it is stuck or has failed, in the words the client or processor gave (a tooltip). */
  reason?: string | null;
  /** What the client or processor actually said, for the tooltip when `reason` is a plainer sentence. */
  reasonFull?: string | null;
  /** The sent download this card matches, so an import case for the same download is known to be on the board. */
  dispatchId?: string;
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
  /** The download in its client, when it is one. */
  queueRef?: { clientId: string; itemId: string };
  /** The download has stopped moving: amber, "not moving", with the reason in its tooltip. */
  stuck?: boolean;
  /** When the download should be done, from the client's own time-left reading (ms since 1970). Absent when it gives none. */
  finishAtMs?: number;
  /** Why Deluno is fetching it, when that can be told from what was recorded. */
  startedBy?: StartedBy;
  /** A line for the route slot when the card has no indexer, client or processor yet: "Asked by Sam". */
  routeNote?: string;
  /** How much the client has downloaded so far, in bytes (a download). */
  downloadedBytes?: number;
  /** Peers (a torrent) or connections (Usenet) the client reports for it; absent when it says none. */
  peers?: number;
  /** The way the client fetches it: "usenet" or "torrent", when it says. */
  protocol?: "usenet" | "torrent";
  /** When the file was handed to the processor, for how long it has been there. */
  handedOverUtc?: string | null;
  /** What the processor last said it is doing with it, when it can be asked (#511). */
  processorStep?: string | null;
  /** The processor's own place in its line for this file, when it says. */
  processorQueuePosition?: number | null;
  /** Where the import is putting it. */
  destination?: "Movies" | "TV Shows";
  /** The import has just finished: the card is held for a moment at 100%, green, before it leaves for the library. */
  landed?: boolean;
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
  // A show's episode ("Lioness.S03E08.1080p...") reads "Lioness S03E08": what follows the code is the release's, not the title's.
  const episode = /^(.*?)[.\s_-]+(S\d{1,2}E\d{1,3})\b/i.exec(cleaned);
  if (episode && episode[1].trim()) return `${titleFromRelease(episode[1])} ${episode[2].toUpperCase()}`;
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

/** Why a download that has stopped is stopped, in the words the client gave (for its tooltip). */
function stuckReason(item: DownloadQueueItem): string | null {
  const message = item.errorMessage?.trim();
  if (message) return message;
  const finding = item.healthFindings?.find((entry) => entry.summary?.trim());
  if (finding) return finding.summary.trim();
  return item.status === "stalled" ? "It has stopped moving in the download client." : null;
}

/** One megabyte per second, in bytes: the unit the clients' speeds are read in. */
const MB = 1024 * 1024;

/**
 * When a download should be done. The client's own time-left reading counts from the moment it was read; a pushed
 * reading newer than that is worked out again from what is left and how fast it is going. Nothing when the client
 * gives no reading (a download that is not moving has no finish time).
 */
export function finishAt(item: Pick<DownloadQueueItem, "etaSeconds" | "sizeBytes">, readAtMs: number, pushed: { progress: number; speedMbps: number; at: number } | undefined, now: number): number | undefined {
  if (pushed && pushed.at > readAtMs && pushed.speedMbps > 0 && item.sizeBytes > 0) {
    const left = item.sizeBytes * (1 - Math.min(100, Math.max(0, pushed.progress)) / 100);
    return now + (left / (pushed.speedMbps * MB)) * 1000;
  }
  const eta = item.etaSeconds;
  if (typeof eta !== "number" || !Number.isFinite(eta) || eta <= 0) return undefined;
  return (Number.isFinite(readAtMs) && readAtMs > 0 ? readAtMs : now) + eta * 1000;
}

/** How long a card whose import has just finished stays on the board as "Imported", before it flies to the library. */
export const LANDED_MS = 4000;

/** Which library a title is going into, from what is known: the title's kind first, then the way the download was filed. */
export function destinationOf(entity: JourneyCard["entity"], mediaType: string | null | undefined): JourneyCard["destination"] {
  if (entity) return entity.kind === "movie" ? "Movies" : "TV Shows";
  const said = (mediaType ?? "").toLowerCase();
  if (!said) return undefined;
  return /tv|series|show|episode/.test(said) ? "TV Shows" : "Movies";
}

/** What a card in Importing says while its import is actually running (a finished download waiting its turn says "Downloaded"). */
export const IMPORTING_NOW = "Importing now";

/** Every card on the Live board, grouped by the caller with `byStage`. */
export function buildJourney({ queue, handoffs, dispatches, live, now, queueCapturedUtc, work = [] }: JourneyInput): JourneyCard[] {
  const cards: JourneyCard[] = [];
  const dispatchByRelease = new Map<string, DownloadDispatchItem>();
  const dispatchByClientItem = new Map<string, DownloadDispatchItem>();
  for (const dispatch of dispatches) {
    const key = releaseKey(dispatch.releaseName);
    const known = dispatchByRelease.get(key);
    if (!known || Date.parse(dispatch.createdUtc) > Date.parse(known.createdUtc)) dispatchByRelease.set(key, dispatch);
    // A release sent again keeps its torrent: the newest record is the one events name, so it wins.
    if (dispatch.torrentHashOrItemId) {
      const itemKey = `${dispatch.downloadClientId}:${dispatch.torrentHashOrItemId}`.toLowerCase();
      const held = dispatchByClientItem.get(itemKey);
      if (!held || Date.parse(dispatch.createdUtc) > Date.parse(held.createdUtc)) dispatchByClientItem.set(itemKey, dispatch);
    }
  }
  const handoffByQueue = new Map(handoffs.filter((item) => item.status.toLowerCase() !== "dismissed").map((item) => [item.queueItemId, item]));
  const seenReleases = new Set<string>();
  // Imports that have said they started and not yet ended: a card for the same release says so, so the card and the band agree.
  const importingNow = new Set(work.filter((item) => item.kind === "import" && item.release).map((item) => releaseKey(item.release!)));
  const storedAt = Date.parse(queueCapturedUtc ?? "");
  const short = shortNamer([
    ...queue.map((item) => item.clientName),
    ...handoffs.map((item) => item.processorName),
    ...dispatches.map((item) => item.downloadClientName)
  ]);

  for (const raw of queue) {
    const reading = live.get(raw.id);
    const fresher = reading && reading.at > (Number.isFinite(storedAt) ? storedAt : 0) && DOWNLOADING.includes(reading.status) ? reading : undefined;
    const item = fresher ? { ...raw, progress: fresher.progress, speedMbps: fresher.speedMbps, status: fresher.status } : raw;
    const dispatch = dispatchByClientItem.get(`${item.clientId}:${item.id}`.toLowerCase()) ?? dispatchByRelease.get(releaseKey(item.releaseName));
    // Imported means in the library: it leaves the journey once the import has landed (and flies to Arrived today), even
    // while the client keeps seeding it. For a moment after the import finishes the card stays, full and green.
    const importedAt = Date.parse(dispatch?.importCompletedUtc ?? "");
    const landed = Number.isFinite(importedAt) && now - importedAt >= 0 && now - importedAt < LANDED_MS;
    if (dispatch?.importCompletedUtc && !landed) continue;
    const handoff = handoffByQueue.get(item.id);
    let stage: Stage | null = null;
    let state: JourneyCard["state"] = { tone: "info", label: "Working" };
    let reason: string | null = null;
    let stuck = false;

    // A finished download that has nobody to share with is reported "stalled" by some clients: it is downloaded, not stuck.
    const downloaded = (item.progress ?? 0) >= 99.95 && DOWNLOADING.includes(item.status);
    const beforeTheProcessor = !downloaded && (item.status === "queued" || DOWNLOADING.includes(item.status));
    if (downloaded && !(handoff && isWithProcessor(handoff.status))) {
      stage = "importing";
      state = { tone: "idle", label: "Downloaded" };
    } else if (beforeTheProcessor && queueItemNeedsAttention(item)) {
      // Stopped moving: it stays where it is, amber, while Deluno finds another copy.
      stage = item.status === "queued" ? "grabbed" : "downloading";
      state = { tone: "warn", label: "Stuck" };
      reason = stuckReason(item);
      stuck = true;
    } else if (item.status === "queued") {
      stage = "grabbed";
      state = { tone: "info", label: "Waiting in the client" };
    } else if (DOWNLOADING.includes(item.status)) {
      stage = "downloading";
      state = item.status === "paused" ? { tone: "idle", label: "Paused" } : item.status === "notTransferring" ? { tone: "info", label: "Checking files" } : { tone: "info", label: "Downloading" };
    } else if (IN_PROCESSOR.includes(item.status) || (handoff && isWithProcessor(handoff.status))) {
      stage = "processor";
      state = handoff ? handoffState(handoff) : item.status === "processingFailed" ? { tone: "bad", label: "Processor failed" } : { tone: "info", label: item.status === "waitingForProcessor" ? BEING_HANDED_OVER : "Being cleaned" };
    } else if (IMPORTING.includes(item.status)) {
      stage = "importing";
      state = item.status === "importFailed" ? { tone: "bad", label: "Import failed" } : item.status === "importQueued" ? { tone: "info", label: "Import queued" } : { tone: "info", label: "Ready to import" };
    }
    if (landed) {
      stage = "importing";
      state = { tone: "ok", label: "Imported" };
      stuck = false;
      reason = null;
    }
    if (!stage) continue;
    if (stage === "importing" && !landed && state.tone !== "bad" && importingNow.has(releaseKey(item.releaseName))) state = { tone: "info", label: IMPORTING_NOW };
    seenReleases.add(releaseKey(item.releaseName));
    const entity = entityOf(dispatch);
    if (stage === "processor" && handoff && (isProcessorFailure(handoff.status) || Boolean(handoff.failureMessage)) && !reason) reason = handoff.failureMessage;
    const moving = stage === "downloading" && !stuck && item.status === "downloading";
    cards.push({
      key: `${item.clientId}:${item.id}`,
      queueRef: { clientId: item.clientId, itemId: item.id },
      stage,
      title: titleFromRelease(item.releaseName || item.title),
      release: item.releaseName || item.title,
      entity,
      state,
      reason: plainReason(reason)?.text ?? null,
      reasonFull: reason,
      dispatchId: dispatch?.id,
      stuck,
      startedBy: startedByOfDispatch(dispatch),
      quality: dispatch?.decisionQuality ?? null,
      sizeBytes: item.sizeBytes,
      indexer: dispatch?.indexerName || (item.indexerName && item.indexerName !== item.clientName ? item.indexerName : null),
      client: short(item.clientName),
      processor: short(handoff?.processorName),
      progress: landed ? 100 : Number.isFinite(item.progress) ? Math.min(100, Math.max(0, item.progress)) : 0,
      downloadedBytes: item.downloadedBytes > 0 ? item.downloadedBytes : undefined,
      peers: item.peers > 0 ? item.peers : undefined,
      protocol: protocolOf(item.protocol),
      handedOverUtc: handoff?.createdUtc ?? null,
      processorStep: handoff?.processor?.message?.trim() || null,
      processorQueuePosition: handoff?.processor?.queuePosition ?? null,
      destination: destinationOf(entity, dispatch?.mediaType ?? item.mediaType),
      landed: landed || undefined,
      speedMbps: item.speedMbps,
      etaSeconds: item.etaSeconds ?? undefined,
      finishAtMs: moving ? finishAt(raw, storedAt, fresher, now) : undefined,
      at: item.addedUtc,
      to: stage === "processor" || stage === "importing" ? "/queue" : cardLink(entity)
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
      state: stage === "importing" ? { tone: "info", label: importingNow.has(releaseKey(handoff.releaseName)) ? IMPORTING_NOW : "Back from the processor" } : handoffState(handoff),
      reason: failed ? plainReason(handoff.failureMessage)?.text ?? null : null,
      reasonFull: failed ? handoff.failureMessage : null,
      dispatchId: dispatch?.id,
      startedBy: startedByOfDispatch(dispatch),
      quality: dispatch?.decisionQuality ?? null,
      indexer: dispatch?.indexerName ?? null,
      client: short(dispatch?.downloadClientName),
      processor: short(handoff.processorName),
      handedOverUtc: handoff.createdUtc,
      processorStep: handoff.processor?.message?.trim() || null,
      processorQueuePosition: handoff.processor?.queuePosition ?? null,
      destination: destinationOf(entity, handoff.mediaType),
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
      state: item.kind === "grab" ? { tone: "info", label: item.client ? `Sending to ${short(item.client)}` : "Sending to the client" } : { tone: "info", label: IMPORTING_NOW },
      startedBy: startedByOfDispatch(dispatch),
      quality: dispatch?.decisionQuality ?? null,
      indexer: dispatch?.indexerName ?? null,
      client: short(item.client ?? dispatch?.downloadClientName),
      destination: destinationOf(entity, dispatch?.mediaType),
      to: item.to
    });
  }

  // What was imported today, newest first: the last stop (the shelf draws today's real imports when the server has them).
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

  return cards;
}

function protocolOf(word: string | null | undefined): JourneyCard["protocol"] {
  const said = (word ?? "").toLowerCase();
  if (said.includes("usenet") || said.includes("nzb")) return "usenet";
  if (said.includes("torrent")) return "torrent";
  return undefined;
}

/** The hand-off has not been submitted yet: the processor has not been given the files, so it is not working on anything. */
export const WAITING_FOR_PROCESSOR = "Waiting for the processor";
const BEING_HANDED_OVER = "Being handed over";

/** True only once the processor really has the title; "waiting to submit" is not working. */
export function processorHasStarted(card: Pick<JourneyCard, "state">): boolean {
  return card.state.label !== WAITING_FOR_PROCESSOR && card.state.label !== BEING_HANDED_OVER;
}

function handoffState(handoff: ProcessorHandoffItem): JourneyCard["state"] {
  const status = handoff.status.toLowerCase();
  if (isProcessorFailure(status) || handoff.failureMessage) return { tone: "bad", label: "Processor failed" };
  if (status === "waiting") return { tone: "info", label: WAITING_FOR_PROCESSOR };
  if (status === "completed") return { tone: "ok", label: "Returned" };
  return { tone: "info", label: "Being cleaned" };
}

/** Moving first in a stage: Live is what is happening now, and a stuck card follows the ones that are moving. */
const toneRank: Record<Tone, number> = { bad: 0, warn: 1, info: 2, ok: 3, idle: 4 };

export function byStage(cards: JourneyCard[]): Record<Stage, JourneyCard[]> {
  const grouped = Object.fromEntries(STAGES.map((stage) => [stage, [] as JourneyCard[]])) as Record<Stage, JourneyCard[]>;
  for (const card of cards) grouped[card.stage].push(card);
  grouped.imported.sort((a, b) => Date.parse(b.at ?? "") - Date.parse(a.at ?? ""));
  // Live is what is happening now: downloads that are moving come first, stuck ones after them (the bell holds the problems).
  grouped.downloading.sort((a, b) => Number(a.stuck ?? false) - Number(b.stuck ?? false) || (b.progress ?? 0) - (a.progress ?? 0));
  for (const stage of ["processor", "importing"] as const) grouped[stage].sort((a, b) => toneRank[a.state.tone] - toneRank[b.state.tone]);
  return grouped;
}

/**
 * The count at each station. The summary counts the whole queue while the lists the cards come from can be shorter, so a
 * station says the larger of the two. A stuck download is still on its station.
 */
export function laneCounts(grouped: Record<Stage, JourneyCard[]>, summary: DashboardSummary | undefined): Record<Stage, number> {
  const downloads = summary?.downloads;
  const pick = (cards: JourneyCard[], fromSummary: number | undefined) => Math.max(cards.length, fromSummary ?? 0);
  return {
    requested: grouped.requested.length,
    grabbed: pick(grouped.grabbed, downloads?.queued),
    downloading: pick(grouped.downloading, downloads?.active),
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
