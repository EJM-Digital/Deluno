import { useSyncExternalStore } from "react";

/**
 * Whether the realtime connection is up right now (#1097).
 *
 * Kept outside React so the shared route cache and any screen can ask without
 * importing the SignalR provider (which imports the cache). While it is up the
 * server pushes every change and no screen needs a polling timer; the timers
 * return as a safety net only while it is down.
 */
let connected = false;
const listeners = new Set<() => void>();

export function setRealtimeConnected(next: boolean) {
  if (connected === next) return;
  connected = next;
  for (const listener of [...listeners]) listener();
}

export function isRealtimeConnected(): boolean {
  return connected;
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** True while the realtime connection is up. Re-renders when it changes. */
export function useRealtimeConnected(): boolean {
  return useSyncExternalStore(subscribe, isRealtimeConnected, () => false);
}

/**
 * A polling interval that exists only as a safety net: `ms` while the realtime
 * connection is down, `false` (no timer at all) while it is up. For any query
 * that sets its own `refetchInterval`.
 */
export function usePollInterval(ms: number | undefined): number | false {
  const up = useRealtimeConnected();
  return !up && ms ? ms : false;
}
