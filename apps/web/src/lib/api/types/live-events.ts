import type { DashboardSectionName, DashboardSummary, TurnedDownGroup } from "./operations";

/**
 * Payloads of the pushed Live events (#1097). Every one carries its own data: a
 * screen applies it as it arrives and never fetches after a nudge. The server
 * sends them to the "live" realtime subject, which a screen joins only while it
 * is on Live or Transfers (`useLiveWatching`, or any `useLiveEvent`), and that is
 * also how the server knows to ask the download clients every second.
 *
 * Mirrors src/Deluno.Contracts/LiveEvents.cs. Timestamps are ISO 8601.
 */

/** Dashboard summary sections that changed, sent as whole sections on the "dashboard" subject. Coalesced within about 100 ms; never a nudge. */
export interface DashboardSummaryUpdatedEvent {
  asOfUtc: string;
  sections: Partial<Pick<DashboardSummary, DashboardSectionName>>;
  /** Sections the server could not build this time; the screen keeps what it already has for them. */
  unavailable: string[];
}

/** One dispatch moved on its journey. `stage` is the timeline word: grab_succeeded, grab_failed, detection_succeeded, client_completed, import_succeeded, import_failed, never_detected, archived, webhook-completed, webhook-failed, client-reported-*, manual_retry_requested. */
export interface JourneyStageChangedEvent {
  dispatchId: string;
  releaseName: string | null;
  mediaType: string | null;
  entityType: string | null;
  entityId: string | null;
  libraryId: string | null;
  stage: string;
  detail: string | null;
  at: string;
}

/** A hand-off to the processor moved on. `state` is the hand-off status (waiting, submitted, accepted, started, completed, failed, rejected, timed-out, stalled, lost, cancelled, released) or a state the processor itself reported (queued, scheduled, working, completed, passed-through, failed). */
export interface ProcessorHandoffChangedEvent {
  handoffId: string;
  queueItemId: string | null;
  libraryId: string | null;
  releaseName: string | null;
  state: string;
  processorName: string | null;
  message: string | null;
  at: string;
}

/** An import recovery case opened or closed. When closed, `kind` and `title` are null. */
export interface RecoveryCaseChangedEvent {
  caseId: string;
  state: "opened" | "closed";
  kind: string | null;
  title: string | null;
  entityType: "movie" | "series" | null;
  entityId: string | null;
  at: string;
}

/** A background job started, finished, failed and will retry, or failed for good. */
export interface JobChangedEvent {
  jobId: string;
  kind: string;
  state: "started" | "finished" | "retrying" | "failed";
  /** Plain-words name of the kind of job. */
  title: string | null;
  libraryId: string | null;
  message: string | null;
  at: string;
}

/** Progress of one library search: `checked` of `planned` titles done while `running`, and `finished` at the end. */
export interface SearchCycleProgressEvent {
  libraryId: string;
  libraryName: string | null;
  mediaType: string | null;
  state: "running" | "finished";
  checked: number;
  planned: number;
  at: string;
}

/** One indexer question, once as it is asked (`phase: "asked"`, no counts yet) and once as it is answered. */
export interface IndexerQueryRecordedEvent {
  indexerId: string;
  indexerName: string | null;
  phase: "asked" | "answered";
  query: string | null;
  resultCount: number | null;
  elapsedMs: number | null;
  failed: boolean;
  failure: string | null;
  at: string;
}

/** A download client became reachable or unreachable, or was paused or resumed. */
export interface ClientStateChangedEvent {
  clientId: string;
  clientName: string | null;
  reachable: boolean;
  paused: boolean | null;
  message: string | null;
  at: string;
}

/** Free space on the drive a file was just imported to. */
export interface DriveSpaceChangedEvent {
  path: string;
  freeBytes: number;
  totalBytes: number | null;
  at: string;
}

/** The clients combined speed in MB/s, sent with every reading while a screen watches (once a second while downloading). */
export interface DownloadSpeedSampleEvent {
  downloadMbps: number;
  uploadMbps: number;
  active: number;
  at: string;
}

/** One search turned releases down (#1098): once per search with any. `items` are those releases, each with its plain group and label. */
export interface ReleasesTurnedDownEvent {
  title: string | null;
  entityType: string | null;
  entityId: string | null;
  atUtc: string;
  items: Array<{ release: string; group: TurnedDownGroup; label: string; newToday?: boolean }>;
}

/** Deluno just used a connection: handed a title to a processor, sent a media server refresh, received a Seerr request, fetched a subtitle or checked a list (#1098). `countToday` includes this use (lists: lists watched); `busy` is a processor's titles right now. */
export interface ConnectionUsedEvent {
  kind: "processor" | "mediaServer" | "requests" | "subtitles" | "lists";
  id: string;
  name: string;
  countToday: number;
  busy: number;
  atUtc: string;
}
