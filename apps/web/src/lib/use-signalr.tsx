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
 * number: `{ seq, name, at, data }`. On connect and on every reconnect the
 * client sends its last-seen `seq` to the hub's `Resume` method. Inside the
 * resume window the gap replays and the client stays caught up; beyond it
 * (or with no prior seq) the hub answers `resync-required` and subscribers
 * registered via `useSignalRResync` are told to refetch from REST. Without
 * this, a client that trusted the stream after a drop would drift out of
 * sync and never recover -- see docs/exec-plans/active/ADR-002-realtime-architecture.md.
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
 *   *Changed                { id } (identity-only state-change family)
 */

import {
  createContext,
  useContext,
  useEffect,
  useRef,
  useState,
  useCallback,
  type ReactNode
} from "react";
import * as signalR from "@microsoft/signalr";

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
  severity: "info" | "warning" | "error" | "success";
  createdUtc: string;
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
export interface EntityChangedEvent {
  id: string;
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
};

export type SignalREventName = keyof EventMap;
export type SignalREventPayload<T extends SignalREventName> = EventMap[T];

/** Wire shape for every push: a monotonic sequence, the event name, a timestamp, and its payload. */
interface RealtimeEnvelope {
  seq: number;
  name: string;
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
    handler: (payload: SignalREventPayload<T>) => void
  ): () => void;
  /** Internal: subscribe to "you may have missed events, refetch from REST". Used by useSignalRResync. */
  subscribeResync(handler: () => void): () => void;
}

const SignalRContext = createContext<SignalRContextValue | null>(null);

/* ── Provider ────────────────────────────────────────────────────── */
const HUB_URL = "/hubs/deluno";
const DEBUG = import.meta.env.VITE_WS_DEBUG === "1";

type AnyHandler = (payload: unknown) => void;

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
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  /** Highest seq applied so far. Reset to 0 whenever the server tells us to resync. */
  const lastSeqRef = useRef(0);

  const subscribe = useCallback(<T extends SignalREventName>(
    event: T,
    handler: (payload: SignalREventPayload<T>) => void
  ) => {
    const map = handlersRef.current;
    if (!map.has(event)) map.set(event, new Set());
    map.get(event)!.add(handler as AnyHandler);
    return () => {
      map.get(event)?.delete(handler as AnyHandler);
    };
  }, []);

  const subscribeResync = useCallback((handler: () => void) => {
    resyncHandlersRef.current.add(handler);
    return () => {
      resyncHandlersRef.current.delete(handler);
    };
  }, []);

  useEffect(() => {
    /** Applies one envelope: advances lastSeq and dispatches to subscribers. Idempotent against replays. */
    const applyEnvelope = (envelope: RealtimeEnvelope) => {
      if (envelope.seq <= lastSeqRef.current) return;
      lastSeqRef.current = envelope.seq;
      if (DEBUG) console.debug(`[WS] ${envelope.name}`, envelope.data);
      const handlers = handlersRef.current.get(envelope.name);
      if (handlers) {
        for (const h of handlers) h(envelope.data);
      }
    };

    /**
     * Asks the hub to fill the gap since lastSeqRef. Inside the resume
     * window this replays what was missed; beyond it (or with no prior
     * seq) the server answers resync-required and every useSignalRResync
     * subscriber is told to refetch from REST.
     */
    const resume = async (connection: signalR.HubConnection) => {
      try {
        const result = await connection.invoke<ResumeResult>("Resume", lastSeqRef.current);
        if (result.status === "Replayed") {
          for (const envelope of result.envelopes) applyEnvelope(envelope);
        } else if (result.status === "ResyncRequired") {
          lastSeqRef.current = 0;
          for (const h of resyncHandlersRef.current) h();
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

    builder.onreconnecting(() => setStatus("reconnecting"));
    builder.onreconnected(() => {
      setStatus("connected");
      void resume(builder);
    });
    builder.onclose(() => setStatus("disconnected"));

    connectionRef.current = builder;

    setStatus("connecting");
    builder.start()
      .then(() => {
        setStatus("connected");
        void resume(builder);
      })
      .catch(() => {
        setStatus("disconnected");
        /* Silently swallow — happens in dev when backend is down */
      });

    return () => {
      void builder.stop();
      connectionRef.current = null;
    };
  }, [accessToken]);

  return (
    <SignalRContext.Provider value={{ status, subscribe, subscribeResync }}>
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
 * Subscribe to a server-push event. Handler is stable across re-renders
 * — no need to memoize it yourself.
 *
 * @example
 * useSignalREvent("DownloadProgress", (e) => setProgress(e.progress));
 */
export function useSignalREvent<T extends SignalREventName>(
  event: T,
  handler: (payload: SignalREventPayload<T>) => void
) {
  const ctx = useContext(SignalRContext);
  const handlerRef = useRef(handler);
  useEffect(() => { handlerRef.current = handler; });

  useEffect(() => {
    if (!ctx) return;
    const stable = (p: SignalREventPayload<T>) => handlerRef.current(p);
    return ctx.subscribe(event, stable);
  }, [ctx, event]);
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
