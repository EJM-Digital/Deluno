/**
 * SignalR real-time context for Deluno.
 *
 * Connects to the .NET backend's `/hubs/deluno` hub and distributes
 * typed server-push events to any component that subscribes via the
 * `useSignalREvent` hook. Handles:
 *   - Auto-connect on mount (with auth token if present)
 *   - Exponential-backoff reconnect (max 30 s)
 *   - Connection state surfaced via `useSignalRStatus()`
 *   - Negotiated transport with automatic fallback (WebSockets -> SSE -> long-polling)
 *   - Dev mode: logs events to console when VITE_WS_DEBUG=1
 *
 * Every push arrives wrapped in an envelope carrying a monotonic sequence
 * number and routing subject: `{ seq, name, subject, at, data }`. On connect and on every reconnect the
 * client sends its last-seen `seq` to the hub's `Resume` method. Inside the
 * resume window the gap replays and the client stays caught up; beyond it
 * (or with no prior seq) the hub answers `resync-required` and subscribers
 * registered via `useSignalRResync` are told to refetch from REST. Without
 * this, a client that trusted the stream after a drop would drift out of
 * sync and never recover -- see docs/exec-plans/active/ADR-002-realtime-architecture.md.
 *
 * Deluno stays live while its tab is in the background (#641). Every envelope
 * is delivered to its subscribers the moment it arrives, hidden or not, so the
 * data behind every screen is already current when the tab is looked at again
 * and there is nothing to catch up on return. Holding events until the tab was
 * visible and then resyncing was tried first and rejected: it still produced a
 * visible refresh the moment the tab came back. Only what exists purely to be
 * seen is held back while hidden -- a toast raised by a realtime event goes
 * through `realtimeToast` (components/shell/toaster.tsx), which drops it
 * rather than replaying it later.
 *
 * Staying connected relies on more than the socket itself staying open
 * (#937). Chrome throttles a hidden, occluded, or minimised tab's own
 * `setInterval`/`setTimeout` timers -- including the one this client's
 * keep-alive ping runs on -- to at most one firing a minute after about
 * five minutes. Left alone, that eventually misses the server's
 * `ClientTimeoutInterval` and gets the connection dropped, which then
 * reconnects and replays or resyncs: a catch-up. A dedicated Worker isn't
 * throttled that way, so a periodic `Ping` invoke driven from the shared
 * worker ticker (lib/live-ticker.ts) keeps reaching the server on schedule
 * regardless of visibility, alongside a server `ClientTimeoutInterval`
 * tolerant of one throttled cycle (see RealtimeServiceCollectionExtensions.cs).
 * A genuine outage still stops both kinds of traffic and is still caught.
 *
 * Events emitted by the hub (mirror backend contracts):
 *   DownloadProgress        { id, title, progress, speedMbps, eta, status }
 *   QueueItemAdded          { id, title, type, status }
 *   QueueItemRemoved        { id }
 *   QueueItemStatusChanged  { id, status, errorMessage }
 *   HealthChanged           { source, status, message }
 *   ActivityEventAdded      { id, message, category, severity, createdUtc }
 *   SearchRunCompleted      { libraryId, libraryName, mediaType, plannedCount, queuedCount, skippedCount, completedUtc }
 *   ImportStateChanged      { jobId, state, entityType, entityId, title, errorMessage, changedUtc }
 *   DispatchGrabAttempt     { dispatchId, releaseName, clientId, clientName, timestamp }
 *   DispatchGrabCompleted   { dispatchId, releaseName, clientId, succeeded, message, timestamp }
 *   DispatchDetected        { dispatchId, releaseName, torrentHash, downloadedBytes, timestamp }
 *   DispatchImportStarted   { dispatchId, releaseName, mediaType, timestamp }
 *   DispatchImportCompleted { dispatchId, releaseName, succeeded, importedPath, failureReason, timestamp }
 *   DashboardSummaryUpdated { asOfUtc, sections, unavailable } (#1097: the changed summary sections themselves, within ~100 ms;
 *                           applied straight into the React Query cache, no fetch afterwards)
 *   DashboardSummaryChanged { asOfUtc } (no longer sent; kept so old subscribers still compile)
 *   Live events on the "live" subject (#1097), each carrying its own data -- see api/types/live-events.ts and
 *   docs/deluno-ui-api-contract.md: JourneyStageChanged, ProcessorHandoffChanged, RecoveryCaseChanged, JobChanged,
 *   SearchCycleProgress, IndexerQueryRecorded, ClientStateChanged, DriveSpaceChanged, DownloadSpeedSample, ReleasesTurnedDown, ConnectionUsed
 *   *Changed                { id } (identity-only state-change family)
 */

import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  useCallback,
  type ReactNode
} from "react";
import * as signalR from "@microsoft/signalr";
import { subscribeTick } from "./live-ticker";
import { queryClient } from "./route-cache";
import { applyDashboardSummaryUpdate } from "./realtime-cache";
import { setRealtimeConnected } from "./realtime-state";
import type {
  ClientStateChangedEvent,
  ConnectionUsedEvent,
  DashboardSummaryUpdatedEvent,
  DownloadSpeedSampleEvent,
  DriveSpaceChangedEvent,
  IndexerQueryRecordedEvent,
  JobChangedEvent,
  JourneyStageChangedEvent,
  ProcessorHandoffChangedEvent,
  RecoveryCaseChangedEvent,
  ReleasesTurnedDownEvent,
  SearchCycleProgressEvent
} from "./api/types/live-events";
import type { ConnectionCheckedEvent, SystemOverviewChangedEvent } from "./api/types/system-overview";
import type { SystemLogEntry } from "./api/types/system-log";
import { firstRealtimePosition, forgetRealtimePosition } from "./realtime-position";

export { useRealtimeConnected } from "./realtime-state";
export type * from "./api/types/live-events";

/* ── Typed event map ─────────────────────────────────────────────── */
export interface DownloadProgressEvent {
  id: string;
  title: string;
  progress: number;
  speedMbps: number;
  eta: string | null;
  status: "downloading" | "paused" | "completed" | "failed";
}

export interface QueueItemAddedEvent {
  id: string;
  title: string;
  type: "movie" | "episode";
  status: string;
}

export interface QueueItemRemovedEvent {
  id: string;
}

export interface HealthChangedEvent {
  source: string;
  status: "healthy" | "degraded" | "offline";
  message: string;
}

export interface ActivityEventAddedEvent {
  id: string;
  message: string;
  category: string;
  /** Plain-words name of the event kind (#1086). */
  label?: string;
  severity: "info" | "warning" | "error" | "success";
  createdUtc: string;
  /** What the event concerns, so a row can be drawn and filtered with no second request (#1179). */
  relatedEntityType?: string | null;
  relatedEntityId?: string | null;
  relatedJobId?: string | null;
  title?: string | null;
  filePath?: string | null;
  sourceName?: string | null;
  reasonGroup?: string | null;
  type?: string;
  source?: string | null;
  outcome?: "good" | "problem";
  /** The event's details, when small enough to ride along. */
  detailsJson?: string | null;
}

export interface AttentionChangedEvent {
  /** What moved the bell's count: recovery, handoff, journey, client, orphans, noRelease. */
  reason: string;
}

export interface ConnectivityChangedEvent {
  isOutage: boolean;
  sinceUtc: string | null;
  checkedUtc: string;
  message: string;
}

export interface DownloadHealthChangedEvent {
  clientId: string | null;
}

export interface QueueItemStatusChangedEvent {
  id: string;
  status: string;
  errorMessage: string | null;
}

export interface SearchRunCompletedEvent {
  libraryId: string;
  libraryName: string;
  mediaType: string;
  plannedCount: number;
  queuedCount: number;
  skippedCount: number;
  completedUtc: string;
}

export interface ImportStateChangedEvent {
  jobId: string;
  state: string;
  entityType: string | null;
  entityId: string | null;
  title: string | null;
  errorMessage: string | null;
  changedUtc: string;
}

/** Identity only: refetch the affected entity instead of trusting event data. */
/** @deprecated No longer sent (#1097): the server pushes `DashboardSummaryUpdated` with the changed sections instead. */
export interface DashboardSummaryChangedEvent {
  asOfUtc: string;
}

/** A Settings area was saved somewhere (#1179): `area` is a name from ConfigAreas in SystemChangeEvents.cs. */
export interface ConfigChangedEvent {
  area: string;
}

export interface EntityChangedEvent {
  id: string;
  /** On a film or show change: the library it is in, when it is in just one. */
  libraryId?: string | null;
}

export interface DispatchGrabAttemptEvent {
  dispatchId: string;
  releaseName: string;
  clientId: string;
  clientName: string;
  timestamp: string;
}

export interface DispatchGrabCompletedEvent {
  dispatchId: string;
  releaseName: string;
  clientId: string;
  succeeded: boolean;
  message: string | null;
  timestamp: string;
}

export interface DispatchDetectedEvent {
  dispatchId: string;
  releaseName: string;
  torrentHash: string | null;
  downloadedBytes: number | null;
  timestamp: string;
}

export interface DispatchImportStartedEvent {
  dispatchId: string;
  releaseName: string;
  mediaType: string;
  timestamp: string;
}

export interface DispatchImportCompletedEvent {
  dispatchId: string;
  releaseName: string;
  succeeded: boolean;
  importedPath: string | null;
  failureReason: string | null;
  timestamp: string;
}

type EventMap = {
  DownloadProgress: DownloadProgressEvent;
  QueueItemAdded: QueueItemAddedEvent;
  QueueItemRemoved: QueueItemRemovedEvent;
  QueueItemStatusChanged: QueueItemStatusChangedEvent;
  HealthChanged: HealthChangedEvent;
  ActivityEventAdded: ActivityEventAddedEvent;
  SearchRunCompleted: SearchRunCompletedEvent;
  ImportStateChanged: ImportStateChangedEvent;
  DispatchGrabAttempt: DispatchGrabAttemptEvent;
  DispatchGrabCompleted: DispatchGrabCompletedEvent;
  DispatchDetected: DispatchDetectedEvent;
  DispatchImportStarted: DispatchImportStartedEvent;
  DispatchImportCompleted: DispatchImportCompletedEvent;
  MovieChanged: EntityChangedEvent;
  SeriesChanged: EntityChangedEvent;
  LibraryChanged: EntityChangedEvent;
  SettingsChanged: EntityChangedEvent;
  QualityProfileChanged: EntityChangedEvent;
  PolicySetChanged: EntityChangedEvent;
  IntakeSourceChanged: EntityChangedEvent;
  AutomationStateChanged: EntityChangedEvent;
  IndexerChanged: EntityChangedEvent;
  DownloadClientChanged: EntityChangedEvent;
  EpisodeChanged: EntityChangedEvent;
  IndexerProxyChanged: EntityChangedEvent;
  DashboardSummaryChanged: DashboardSummaryChangedEvent;
  DashboardSummaryUpdated: DashboardSummaryUpdatedEvent;
  JourneyStageChanged: JourneyStageChangedEvent;
  ProcessorHandoffChanged: ProcessorHandoffChangedEvent;
  RecoveryCaseChanged: RecoveryCaseChangedEvent;
  JobChanged: JobChangedEvent;
  SearchCycleProgress: SearchCycleProgressEvent;
  IndexerQueryRecorded: IndexerQueryRecordedEvent;
  ClientStateChanged: ClientStateChangedEvent;
  DriveSpaceChanged: DriveSpaceChangedEvent;
  ReleasesTurnedDown: ReleasesTurnedDownEvent;
  ConnectionUsed: ConnectionUsedEvent;
  DownloadSpeedSample: DownloadSpeedSampleEvent;
  SystemOverviewChanged: SystemOverviewChangedEvent;
  SystemLogEntry: SystemLogEntry;
  ConnectionChecked: ConnectionCheckedEvent;
  AttentionChanged: AttentionChangedEvent;
  ConnectivityChanged: ConnectivityChangedEvent;
  DownloadHealthChanged: DownloadHealthChangedEvent;
  BackupChanged: EntityChangedEvent;
  UpdateStatusChanged: EntityChangedEvent;
  RecycleBinChanged: EntityChangedEvent;
  SystemTaskChanged: EntityChangedEvent;
  MonitoringChanged: EntityChangedEvent;
  SystemDatabasesChanged: EntityChangedEvent;
  OutboundThrottleChanged: EntityChangedEvent;
  CompanionJobChanged: EntityChangedEvent;
  BundledComponentChanged: EntityChangedEvent;
  ConfigChanged: ConfigChangedEvent;
};

export type SignalREventName = keyof EventMap;
export type SignalREventPayload<T extends SignalREventName> = EventMap[T];

/** Subjects a screen can join for the lifetime of its event subscriptions. */
export type RealtimeSubject = string | readonly string[];

export const RealtimeGroups = {
  Dashboard: "dashboard",
  Queue: "queue",
  Activity: "activity",
  /** Joined only while Live or Transfers is open: tells the server someone is watching, so it reads the download clients every second (#1097). */
  Live: "live",
  Library: (libraryId: string) => `library:${libraryId}`,
  /** One film or show: a title page joins this and hears only about its own title (#1179). */
  Title: (titleId: string) => `title:${titleId}`
} as const;

/** Wire shape for every push: a monotonic sequence, the event name, a timestamp, and its payload. */
interface RealtimeEnvelope {
  seq: number;
  name: string;
  subject: string;
  at: string;
  data: unknown;
}

type ResumeStatus = "CaughtUp" | "Replayed" | "ResyncRequired";

interface ResumeResult {
  status: ResumeStatus;
  envelopes: RealtimeEnvelope[];
}

/* ── Connection state ────────────────────────────────────────────── */
export type SignalRStatus = "connecting" | "connected" | "reconnecting" | "disconnected";

/* ── Context ─────────────────────────────────────────────────────── */
interface SignalRContextValue {
  status: SignalRStatus;
  /** Internal: subscribe to an event. Used by useSignalREvent. */
  subscribe<T extends SignalREventName>(
    event: T,
    subjects: RealtimeSubject,
    handler: (payload: SignalREventPayload<T>) => void
  ): () => void;
  /** Internal: subscribe to "you may have missed events, refetch from REST". Used by useSignalRResync. */
  subscribeResync(handler: () => void): () => void;
}

const SignalRContext = createContext<SignalRContextValue | null>(null);

/* ── Provider ────────────────────────────────────────────────────── */
const HUB_URL = "/hubs/deluno";
const DEBUG = import.meta.env.VITE_WS_DEBUG === "1";
/** Waits between attempts to bring the connection up; the last one repeats for as long as the page is open. */
const START_RETRY_DELAYS_MS = [0, 2_000, 5_000, 10_000, 30_000] as const;

type AnyHandler = (payload: unknown) => void;

function normalizeSubjects(subjects: RealtimeSubject): string[] {
  return typeof subjects === "string"
    ? [subjects]
    : [...new Set(subjects)];
}

export function SignalRProvider({
  children,
  accessToken
}: {
  children: ReactNode;
  accessToken?: string | null;
}) {
  const [status, setStatus] = useState<SignalRStatus>("connecting");
  const handlersRef = useRef(new Map<string, Set<AnyHandler>>());
  const resyncHandlersRef = useRef(new Set<() => void>());
  const subjectCountsRef = useRef(new Map<string, number>());

  // The route cache and every screen ask this one flag whether their safety-net timers may run (#1097).
  useEffect(() => {
    setRealtimeConnected(status === "connected");
  }, [status]);
  useEffect(() => () => setRealtimeConnected(false), []);
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  /** Highest seq applied so far. Reset to 0 whenever the server tells us to resync. */
  const lastSeqRef = useRef(0);

  /**
   * Brings the hub's subjects in line with what screens want, whenever either changes (#1178).
   * Set by the connection effect below; a no-op while there is no connection. It never drops a
   * request: a subject asked for while the connection is starting, down or reconnecting is
   * simply sent the moment it is up, and every change is sent in order.
   */
  const reconcileRef = useRef<() => Promise<void>>(async () => {});

  const subscribe = useCallback(<T extends SignalREventName>(
    event: T,
    subjects: RealtimeSubject,
    handler: (payload: SignalREventPayload<T>) => void
  ) => {
    const requestedSubjects = normalizeSubjects(subjects);
    const map = handlersRef.current;
    if (!map.has(event)) map.set(event, new Set());
    map.get(event)!.add(handler as AnyHandler);
    requestedSubjects.forEach((subject) => {
      subjectCountsRef.current.set(subject, (subjectCountsRef.current.get(subject) ?? 0) + 1);
    });
    void reconcileRef.current();

    return () => {
      map.get(event)?.delete(handler as AnyHandler);
      requestedSubjects.forEach((subject) => {
        const count = subjectCountsRef.current.get(subject) ?? 0;
        if (count <= 1) {
          subjectCountsRef.current.delete(subject);
        } else {
          subjectCountsRef.current.set(subject, count - 1);
        }
      });
      void reconcileRef.current();
    };
  }, []);

  const subscribeResync = useCallback((handler: () => void) => {
    resyncHandlersRef.current.add(handler);
    return () => {
      resyncHandlersRef.current.delete(handler);
    };
  }, []);

  /**
   * Calls every handler subscribed to this envelope's event, with the raw
   * payload -- the one place a live envelope reaches subscriber code, whether
   * or not the tab is on screen (#641).
   */
  const deliver = useCallback((envelope: RealtimeEnvelope) => {
    if (DEBUG) console.debug(`[WS] ${envelope.name}`, envelope.data);
    // Pushed summary sections go straight into the cache, before any subscriber runs, so a screen
    // reading the summary query is already current and never fetches after a nudge (#1097).
    if (envelope.name === "DashboardSummaryUpdated") {
      applyDashboardSummaryUpdate(queryClient, envelope.data as DashboardSummaryUpdatedEvent);
    }
    const handlers = handlersRef.current.get(envelope.name);
    if (handlers) {
      for (const h of handlers) h(envelope.data);
    }
  }, []);

  useEffect(() => {
    /** Subjects this connection has joined on the hub. Empty for every new connection, since the hub forgets a dropped one. */
    const joined = new Set<string>();
    let disposed = false;
    let chain: Promise<void> = Promise.resolve();
    let retryTimer: ReturnType<typeof setTimeout> | null = null;

    /** Sends one batch; if the hub refuses the batch, sends each subject alone so one bad subject cannot cost the rest. */
    const send = async (method: "Subscribe" | "Unsubscribe", subjects: string[], done: (subject: string) => void) => {
      let allSent = true;
      try {
        await builder.invoke(method, subjects);
        subjects.forEach(done);
        return true;
      } catch (error) {
        if (DEBUG) console.debug(`[WS] ${method.toLowerCase()} failed`, error);
        if (subjects.length === 1) return false;
      }
      for (const subject of subjects) {
        try {
          await builder.invoke(method, [subject]);
          done(subject);
        } catch {
          allSent = false;
        }
      }
      return allSent;
    };

    const reconcileNow = async () => {
      if (disposed || builder.state !== signalR.HubConnectionState.Connected) return;
      const wanted = subjectCountsRef.current;
      const toAdd = [...wanted.keys()].filter((subject) => !joined.has(subject));
      const toRemove = [...joined].filter((subject) => !wanted.has(subject));
      let ok = true;
      if (toRemove.length > 0) ok = (await send("Unsubscribe", toRemove, (subject) => joined.delete(subject))) && ok;
      if (toAdd.length > 0) ok = (await send("Subscribe", toAdd, (subject) => joined.add(subject))) && ok;
      if (!ok && !disposed && retryTimer === null) {
        // A refused or interrupted request is tried again, never forgotten.
        retryTimer = globalThis.setTimeout(() => {
          retryTimer = null;
          void reconcile();
        }, 2_000);
      }
    };

    /** Every subject change goes through this one queue, so requests reach the hub in the order they were made. */
    const reconcile = () => {
      chain = chain.then(reconcileNow, reconcileNow);
      return chain;
    };
    reconcileRef.current = reconcile;

    /**
     * Applies one envelope: advances lastSeq and dispatches it. Idempotent
     * against replays, since the seq check runs first.
     */
    const applyEnvelope = (envelope: RealtimeEnvelope) => {
      if (envelope.seq <= lastSeqRef.current) return;
      lastSeqRef.current = envelope.seq;
      deliver(envelope);
    };

    /**
     * Joins every subject the screens want (nothing is joined on a new connection),
     * then asks the hub to fill the gap since lastSeqRef. Inside the resume
     * window this replays what was missed; beyond it (or with no prior
     * seq) the server answers resync-required and every useSignalRResync
     * subscriber is told to refetch from REST.
     */
    let attachedBefore = false;
    const attach = async (connection: signalR.HubConnection) => {
      joined.clear();
      await reconcile();
      // The first connect of a page has nothing to catch up on: every screen has just loaded
      // its data, so the server's answer to "resume from nothing" must not refetch it all (#1179).
      const firstConnect = !attachedBefore;
      attachedBefore = true;
      // ...except for what was published after the page read its first data and before this join (#1191): ask the hub to
      // replay from where the stream stood at that first read. If its buffer no longer reaches back that far, it says resync.
      const since = firstConnect && lastSeqRef.current === 0 ? firstRealtimePosition() : 0;
      if (since > 0) lastSeqRef.current = since;
      if (firstConnect) forgetRealtimePosition();
      try {
        const result = await connection.invoke<ResumeResult>("Resume", lastSeqRef.current, [...subjectCountsRef.current.keys()]);
        if (result.status === "Replayed") {
          for (const envelope of result.envelopes) applyEnvelope(envelope);
        } else if (result.status === "ResyncRequired") {
          lastSeqRef.current = 0;
          if (!firstConnect || since > 0) for (const h of resyncHandlersRef.current) h();
        }
      } catch (error) {
        if (DEBUG) console.debug("[WS] resume failed", error);
      }
    };

    const builder = new signalR.HubConnectionBuilder()
      .withUrl(HUB_URL, {
        // No forced transport and no skipped negotiation: let SignalR
        // negotiate and degrade WebSockets -> SSE -> long-polling. See
        // docs/exec-plans/active/ADR-002-realtime-architecture.md.
        accessTokenFactory: accessToken ? () => accessToken : undefined
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(DEBUG ? signalR.LogLevel.Information : signalR.LogLevel.None)
      .build();

    builder.on("RealtimeEvent", (envelope: RealtimeEnvelope) => applyEnvelope(envelope));

    builder.onreconnecting(() => {
      joined.clear();
      if (!disposed) setStatus("reconnecting");
    });
    builder.onreconnected(() => {
      if (disposed) return;
      setStatus("connected");
      void attach(builder);
    });
    // Declared before the handlers that use it; set once the start loop exists.
    let restart: () => void = () => {};
    builder.onclose(() => {
      joined.clear();
      if (disposed) return;
      setStatus("disconnected");
      restart();
    });

    connectionRef.current = builder;

    // Worker-driven keep-alive nudge (#937): the SignalR client's own
    // keep-alive ping runs on a page timer, which Chrome throttles to at
    // most one firing a minute once this tab has been hidden, occluded, or
    // minimised for about five minutes -- long enough for the ping to miss
    // the server's ClientTimeoutInterval and get the connection dropped
    // (see RealtimeServiceCollectionExtensions.cs for the matching
    // server-side timeouts). A dedicated Worker isn't subject to that
    // throttling, so invoking the hub's no-op `Ping` off its clock keeps
    // reaching the server on schedule regardless of visibility. Any
    // invocation counts as client activity to the server, so this alone is
    // enough -- it does not depend on the framework's own keep-alive timer
    // also getting through.
    // display-clock: the hub keep-alive ping; it fetches no data.
    const stopPingNudge = subscribeTick(20_000, () => {
      if (builder.state === signalR.HubConnectionState.Connected) {
        void builder.invoke("Ping").catch(() => {
          if (DEBUG) console.debug("[WS] ping nudge failed");
        });
      }
    });

    /**
     * Brings the connection up, and keeps trying for as long as the page is open: SignalR's own
     * automatic reconnect gives up after its last delay, and a screen left open overnight must
     * still come back by itself (#1178). Only one loop runs at a time.
     */
    let starting = false;
    const startWithRetry = async () => {
      if (starting) return;
      starting = true;
      try {
        for (let attempt = 0; !disposed; attempt++) {
          const delay = START_RETRY_DELAYS_MS[Math.min(attempt, START_RETRY_DELAYS_MS.length - 1)];
          if (delay > 0) await new Promise<void>((resolve) => globalThis.setTimeout(resolve, delay));
          if (disposed) return;

          try {
            await builder.start();
            if (disposed) {
              await builder.stop();
              return;
            }
            setStatus("connected");
            void attach(builder);
            return;
          } catch {
            if (!disposed) setStatus("disconnected");
          }
        }
      } finally {
        starting = false;
      }
    };
    restart = () => void startWithRetry();

    setStatus("connecting");
    void startWithRetry();

    return () => {
      disposed = true;
      if (retryTimer !== null) globalThis.clearTimeout(retryTimer);
      reconcileRef.current = async () => {};
      stopPingNudge();
      void builder.stop();
      connectionRef.current = null;
    };
  }, [accessToken, deliver]);

  // One stable object per status, so a screen's subscription is not torn down and rebuilt by an unrelated render.
  const contextValue = useMemo(() => ({ status, subscribe, subscribeResync }), [status, subscribe, subscribeResync]);

  return (
    <SignalRContext.Provider value={contextValue}>
      {children}
    </SignalRContext.Provider>
  );
}

/* ── Hooks ───────────────────────────────────────────────────────── */

/** Returns the current WebSocket connection status. */
export function useSignalRStatus(): SignalRStatus {
  const ctx = useContext(SignalRContext);
  return ctx?.status ?? "disconnected";
}

/**
 * Whether the Deluno tab is the one currently on screen (#641), for a
 * consumer that wants to hold back its own purely visual work -- an
 * animation or highlight -- while nobody is looking. Data never waits on
 * this. Does not require a `SignalRProvider` ancestor -- it only wraps the
 * Page Visibility API.
 */
export function useIsTabVisible(): boolean {
  const [visible, setVisible] = useState(
    () => typeof document === "undefined" || document.visibilityState !== "hidden"
  );

  useEffect(() => {
    if (typeof document === "undefined") return;
    const onVisibilityChange = () => setVisible(document.visibilityState !== "hidden");
    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => document.removeEventListener("visibilitychange", onVisibilityChange);
  }, []);

  return visible;
}

/**
 * Subscribe to a server-push event. Handler is stable across re-renders
 * — no need to memoize it yourself.
 *
 * @example
 * useSignalREvent("DownloadProgress", RealtimeGroups.Queue, (e) => setProgress(e.progress));
 */
export function useSignalREvent<T extends SignalREventName>(
  event: T,
  subjects: RealtimeSubject,
  handler: (payload: SignalREventPayload<T>) => void
) {
  const ctx = useContext(SignalRContext);
  const subjectKey = typeof subjects === "string" ? subjects : subjects.join("\u001f");
  const stableSubjects = useMemo(
    () => [...new Set(subjectKey ? subjectKey.split("\u001f") : [])],
    [subjectKey]
  );
  const handlerRef = useRef(handler);
  useEffect(() => { handlerRef.current = handler; });

  useEffect(() => {
    if (!ctx) return;
    const stable = (p: SignalREventPayload<T>) => handlerRef.current(p);
    return ctx.subscribe(event, stableSubjects, stable);
  }, [ctx, event, stableSubjects]);
}

/**
 * The same as `useSignalREvent` for several events at once (#1179): one subscription per name,
 * one handler, joined on the same subjects. The names are read when the component first renders
 * with this list, so pass a constant list.
 */
export function useSignalREvents(
  events: readonly SignalREventName[],
  subjects: RealtimeSubject,
  handler: (name: SignalREventName, payload: unknown) => void
) {
  const ctx = useContext(SignalRContext);
  const subjectKey = typeof subjects === "string" ? subjects : subjects.join("");
  const eventKey = events.join("");
  const stableSubjects = useMemo(
    () => [...new Set(subjectKey ? subjectKey.split("") : [])],
    [subjectKey]
  );
  const handlerRef = useRef(handler);
  useEffect(() => { handlerRef.current = handler; });

  useEffect(() => {
    if (!ctx || !eventKey) return;
    const stops = (eventKey.split("") as SignalREventName[]).map((name) =>
      ctx.subscribe(name, stableSubjects, (payload: unknown) => handlerRef.current(name, payload))
    );
    return () => { for (const stop of stops) stop(); };
  }, [ctx, eventKey, stableSubjects]);
}

/**
 * Called when a reconnect's gap was too large to replay (or there was no
 * prior sequence at all) and the server has told the client to resync.
 * Subscribers should refetch their data from REST. Handler is stable
 * across re-renders — no need to memoize it yourself.
 *
 * @example
 * useSignalRResync(() => queryClient.invalidateQueries());
 */
export function useSignalRResync(handler: () => void) {
  const ctx = useContext(SignalRContext);
  const handlerRef = useRef(handler);
  useEffect(() => { handlerRef.current = handler; });

  useEffect(() => {
    if (!ctx) return;
    const stable = () => handlerRef.current();
    return ctx.subscribeResync(stable);
  }, [ctx]);
}

/* ── Live (#1097) ────────────────────────────────────────────────── */

/**
 * The subject each event travels on. A screen that listens for one of these
 * through `useLiveEvent` joins that subject plus the "live" one, so the server
 * knows someone is watching.
 */
const LIVE_EVENT_SUBJECTS = {
  DashboardSummaryUpdated: RealtimeGroups.Dashboard,
  HealthChanged: RealtimeGroups.Dashboard,
  DownloadProgress: RealtimeGroups.Queue,
  QueueItemAdded: RealtimeGroups.Queue,
  QueueItemRemoved: RealtimeGroups.Queue,
  QueueItemStatusChanged: RealtimeGroups.Queue,
  ImportStateChanged: RealtimeGroups.Queue,
  DispatchGrabAttempt: RealtimeGroups.Queue,
  DispatchGrabCompleted: RealtimeGroups.Queue,
  DispatchDetected: RealtimeGroups.Queue,
  DispatchImportStarted: RealtimeGroups.Queue,
  DispatchImportCompleted: RealtimeGroups.Queue,
  ActivityEventAdded: RealtimeGroups.Activity,
  JourneyStageChanged: RealtimeGroups.Live,
  ProcessorHandoffChanged: RealtimeGroups.Live,
  RecoveryCaseChanged: RealtimeGroups.Live,
  JobChanged: RealtimeGroups.Live,
  SearchCycleProgress: RealtimeGroups.Live,
  IndexerQueryRecorded: RealtimeGroups.Live,
  ClientStateChanged: RealtimeGroups.Live,
  DriveSpaceChanged: RealtimeGroups.Live,
  ReleasesTurnedDown: RealtimeGroups.Live,
  ConnectionUsed: RealtimeGroups.Live,
  DownloadSpeedSample: RealtimeGroups.Live,
  SystemOverviewChanged: RealtimeGroups.Dashboard,
  SystemLogEntry: RealtimeGroups.Dashboard,
  ConnectionChecked: RealtimeGroups.Dashboard,
  AttentionChanged: RealtimeGroups.Dashboard,
  ConnectivityChanged: RealtimeGroups.Dashboard,
  DownloadHealthChanged: RealtimeGroups.Dashboard
} as const satisfies Partial<Record<SignalREventName, string>>;

export type LiveEventName = keyof typeof LIVE_EVENT_SUBJECTS;

/**
 * Subscribes to a pushed event on the subject the server sends it on, and
 * tells the server this screen is watching Live (#1097): while at least one
 * screen is, and something is downloading, the download clients are read every
 * second and each reading is pushed. The handler is stable across re-renders.
 *
 * @example
 * useLiveEvent("JobChanged", (job) => setJobs((all) => upsert(all, job)));
 */
export function useLiveEvent<T extends LiveEventName>(
  event: T,
  handler: (payload: SignalREventPayload<T>) => void
) {
  useSignalREvent(event, [LIVE_EVENT_SUBJECTS[event], RealtimeGroups.Live], handler);
}

/**
 * Marks this screen as watching Live or Transfers for as long as it is mounted,
 * without listening for anything in particular (#1097). Use on a screen that
 * shows download progress through `useSignalREvent` but is not a `useLiveEvent` consumer.
 */
export function useLiveWatching() {
  useSignalREvent("DownloadSpeedSample", RealtimeGroups.Live, noop);
}

function noop() {}
