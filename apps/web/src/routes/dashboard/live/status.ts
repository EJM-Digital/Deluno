/** The Live tab's connection state in plain words, from the realtime connection. */
import type { SignalRStatus } from "../../../lib/use-signalr";
import type { Tone } from "../../../lib/status-tones";

export interface Heartbeat {
  tone: Tone;
  label: string;
  /** True only while connected: the pulse means the stream is really arriving. */
  beating: boolean;
}

export function heartbeatState(status: SignalRStatus): Heartbeat {
  switch (status) {
    case "connected": return { tone: "ok", label: "Live", beating: true };
    case "connecting": return { tone: "info", label: "Connecting", beating: false };
    case "reconnecting": return { tone: "warn", label: "Reconnecting", beating: false };
    default: return { tone: "bad", label: "Offline", beating: false };
  }
}
