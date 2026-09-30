/**
 * The Live tab's heartbeat (#1096): a pulse that is lit only while the realtime
 * connection is really up, the words for its state, and "updated N s ago"
 * counting up from when the summary last arrived. Reconnecting and offline are
 * said in words and colour, and the pulse stops.
 */
import { cn } from "../../../lib/utils";
import { motionAllowed } from "../../../lib/page-motion";
import { useSignalRStatus } from "../../../lib/use-signalr";
import { updatedAgo } from "./countdown";
import { useNow } from "./hooks";
import { heartbeatState } from "./status";

const TONE_TEXT = { ok: "text-success", warn: "text-warning", bad: "text-destructive", info: "text-info", idle: "text-muted-foreground" } as const;
const TONE_FILL = { ok: "bg-success", warn: "bg-warning", bad: "bg-destructive", info: "bg-info", idle: "bg-muted-foreground/60" } as const;

/** The pulse and the word for the connection, for the strip's value line. */
export function HeartbeatValue() {
  const state = heartbeatState(useSignalRStatus());
  return (
    <span className={cn("inline-flex items-center gap-2", TONE_TEXT[state.tone])}>
      <span aria-hidden className="relative inline-flex h-2.5 w-2.5 items-center justify-center">
        {state.beating && motionAllowed() ? <span className={cn("live-beat absolute inset-0 rounded-full", TONE_FILL[state.tone])} /> : null}
        <span className={cn("relative h-2.5 w-2.5 rounded-full", TONE_FILL[state.tone])} />
      </span>
      {state.label}
    </span>
  );
}

/** "updated 4 s ago", ticking every second. */
export function UpdatedAgo({ updatedAt }: { updatedAt: number }) {
  const now = useNow(1000);
  return <>{updatedAgo(updatedAt, now)}</>;
}
