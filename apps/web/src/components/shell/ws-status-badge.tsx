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
}

/** Connected has no entry: it is silent. */
const STATUS_CONFIG: Record<Exclude<SignalRStatus, "connected">, Config> = {
  connecting: {
    dot: "bg-muted-foreground",
    label: "Connecting…",
    ring: "border-hairline bg-card text-muted-foreground"
  },
  reconnecting: {
    dot: "bg-warning animate-pulse",
    label: "Not live: reconnecting",
    ring: "border-warning/25 bg-warning/10 text-warning"
  },
  disconnected: {
    dot: "bg-destructive",
    label: "Not live: trying again",
    ring: "border-destructive/25 text-destructive"
  }
};

export function WsStatusBadge({ className }: { className?: string }) {
  const status = useSignalRStatus();

  if (status === "connected") return null;

  const cfg = STATUS_CONFIG[status];

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
