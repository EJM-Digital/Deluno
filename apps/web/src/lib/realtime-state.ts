import { useSyncExternalStore } from "react";

/**
 * Whether the realtime connection is up right now (#1097).
 *
 * Kept outside React so the shared route cache and any screen can ask without
 * importing the SignalR provider (which imports the cache). While it is up the
 * server pushes every change and no screen polls. While it is down a screen says
 * so (the Not live badge) and catches up once on reconnect (#1179).
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
