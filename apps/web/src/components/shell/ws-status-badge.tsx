/**
 * WebSocket connection status badge — appears in the topbar.
 *
 * · connected    → silent (no visual noise in steady state)
 * · reconnecting → amber pulse dot + "Not live: reconnecting"
 * · disconnected → red dot + "Not live: trying again"
 * · connecting   → subtle spinner
 *
 * Also acts as an ARIA live region so screen readers announce
 * connection state changes.
 */

import { cn } from "../../lib/utils";
import { useSignalRStatus, type SignalRStatus } from "../../lib/use-signalr";

interface Config {
  dot: string;
  label: string;
  ring: string;
  animate: boolean;
  visible: boolean;
}

const STATUS_CONFIG: Record<SignalRStatus, Config> = {
  connected: {
    dot: "bg-success",
    label: "Live",
    ring: "border-success/25 bg-success/10 text-success",
    animate: false,
    visible: false
  },
  connecting: {
    dot: "bg-muted-foreground",
    label: "Connecting…",
    ring: "border-hairline bg-card text-muted-foreground",
    animate: true,
    visible: true
  },
  reconnecting: {
    dot: "bg-warning animate-pulse",
    label: "Not live: reconnecting",
    ring: "border-warning/25 bg-warning/10 text-warning",
    animate: false,
    visible: true
  },
  disconnected: {
    dot: "bg-destructive",
    label: "Not live: trying again",
    ring: "border-destructive/25 text-destructive",
    animate: false,
    visible: true
  }
};

export function WsStatusBadge({ className }: { className?: string }) {
  const status = useSignalRStatus();
  const cfg = STATUS_CONFIG[status];

  if (!cfg.visible) return null;

  return (
    <span
      role="status"
      aria-live="polite"
      aria-label={status === "connecting" ? cfg.label : `${cfg.label}. Live updates are paused, so the numbers on screen may be out of date.`}
      title={status === "connecting" ? undefined : "Live updates are paused, so the numbers on screen may be out of date. Deluno keeps trying to reconnect."}
      data-realtime-status={status}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 text-(length:--shell-subtle-size) font-medium",
        cfg.ring,
        className
      )}
    >
      <span className={cn("h-1.5 w-1.5 rounded-full", cfg.dot)} />
      {cfg.label}
    </span>
  );
}

/**
 * Simpler dot-only variant for tight spaces (e.g. beside the notification bell).
 */
export function WsStatusDot({ className }: { className?: string }) {
  const status = useSignalRStatus();

  if (status === "connected") return null;

  const colors: Record<SignalRStatus, string> = {
    connected: "bg-success",
    connecting: "bg-muted-foreground",
    reconnecting: "bg-warning animate-pulse",
    disconnected: "bg-destructive"
  };

  return (
    <span
      role="status"
      aria-live="polite"
      aria-label={`Connection: ${status}`}
      className={cn("h-2 w-2 rounded-full shadow-xs", colors[status], className)}
    />
  );
}
