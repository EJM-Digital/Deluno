/**
 * SignalR real-time context for Deluno.
 *
 * Connects to the .NET backend's `/hubs/deluno` hub and distributes
 * typed server-push events to any component that subscribes via the
 * `useSignalREvent` hook. Handles:
 *   - Auto-connect on mount (with auth token if present)
 *   - Exponential-backoff reconnect (max 30 s)
 *   - Connection state surfaced via `useSignalRStatus()`
 *   - WebSockets-only transport for predictable low-overhead live updates
 *   - Dev mode: logs events to console when VITE_WS_DEBUG=1
 *
 * Events emitted by the hub (mirror backend contracts):
 *   DownloadProgress   { id, title, progress, speedMbps, eta }
 *   QueueItemAdded     { id, title, type, status }
 *   QueueItemRemoved   { id }
 *   HealthChanged      { source, status, message }
 *   ActivityEventAdded { id, message, category, severity, createdUtc }
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

export interface SearchProgressEvent {
  id: string;
  title: string;
  progress: number; // 0-100
  totalResults: number;
  eta: string | null;
  status: "searching" | "completed" | "failed";
}

export interface ImportStatusEvent {
  id: string;
  releaseName: string;
  progress: number; // 0-100
  status: "importing" | "completed" | "failed";
  importedPath?: string;
  failureReason?: string;
}

export interface AutomationStatusEvent {
  automationId: string;
  libraryId: string;
  status: "queued" | "running" | "completed" | "failed";
  itemsProcessed: number;
  totalItems: number;
  lastRunUtc: string;
  nextRunUtc: string;
}

type EventMap = {
  DownloadProgress: DownloadProgressEvent;
  QueueItemAdded: QueueItemAddedEvent;
  QueueItemRemoved: QueueItemRemovedEvent;
  HealthChanged: HealthChangedEvent;
  ActivityEventAdded: ActivityEventAddedEvent;
  SearchProgress: SearchProgressEvent;
  ImportStatus: ImportStatusEvent;
  AutomationStatus: AutomationStatusEvent;
  DownloadTelemetryChanged: Record<string, never>;
};

export type SignalREventName = keyof EventMap;
export type SignalREventPayload<T extends SignalREventName> = EventMap[T];

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
  const connectionRef = useRef<signalR.HubConnection | null>(null);

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

  useEffect(() => {
    const builder = new signalR.HubConnectionBuilder()
      .withUrl(HUB_URL, {
        accessTokenFactory: accessToken ? () => accessToken : undefined,
        transport: signalR.HttpTransportType.WebSockets,
        skipNegotiation: true
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(DEBUG ? signalR.LogLevel.Information : signalR.LogLevel.None)
      .build();

    /* Register handlers for every event name we know */
    const eventNames: SignalREventName[] = [
      "DownloadProgress",
      "QueueItemAdded",
      "QueueItemRemoved",
      "HealthChanged",
      "ActivityEventAdded"
    ];

    for (const name of eventNames) {
      builder.on(name, (payload: unknown) => {
        if (DEBUG) console.debug(`[WS] ${name}`, payload);
        const handlers = handlersRef.current.get(name);
        if (handlers) {
          for (const h of handlers) h(payload);
        }
      });
    }

    builder.onreconnecting(() => setStatus("reconnecting"));
    builder.onreconnected(() => setStatus("connected"));
    builder.onclose(() => setStatus("disconnected"));

    connectionRef.current = builder;

    builder.start()
      .then(() => setStatus("connected"))
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
    <SignalRContext.Provider value={{ status, subscribe }}>
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
