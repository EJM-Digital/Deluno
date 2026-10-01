/**
 * The two pushed events that move the lower half of Live (#1098): releases turned
 * down (a bar flashes as the decision is made) and a connection used (its tile
 * lights up). Each is kept only for what the screen needs: the turn-downs newer
 * than the stored counts, and the newest count for each connection.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { DashboardLiveExtras, LiveConnection, TurnedDownGroup } from "../../../lib/api/types/operations";
import type { ConnectionUsedEvent, ReleasesTurnedDownEvent } from "../../../lib/api/types/live-events";
import { useLiveEvent } from "../../../lib/use-signalr";
import { applyConnectionEvents, connectionKey, sortConnections } from "./connections";
import { mergeTurnedDown, LATEST_LIMIT, type TurnedDownView } from "./turned-down";

/** How long a bar or a tile stays lit after it moved. */
const LIT_MS = 1400;

/** Marks keys as lit for a moment each. */
function useLit(): [ReadonlySet<string>, (keys: Iterable<string>) => void] {
  const [lit, setLit] = useState<ReadonlySet<string>>(() => new Set());
  const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>());
  const light = useCallback((keys: Iterable<string>) => {
    const added = [...keys];
    if (added.length === 0) return;
    setLit((current) => new Set([...current, ...added]));
    for (const key of added) {
      const known = timers.current.get(key);
      if (known) clearTimeout(known);
      timers.current.set(key, setTimeout(() => {
        timers.current.delete(key);
        setLit((current) => {
          const next = new Set(current);
          next.delete(key);
          return next;
        });
      }, LIT_MS));
    }
  }, []);
  useEffect(() => () => {
    for (const timer of timers.current.values()) clearTimeout(timer);
  }, []);
  return [lit, light];
}

/** What was turned down today, the stored counts with every release pushed since, and the reasons that just moved. */
export function useTurnedDown(extras: DashboardLiveExtras | null | undefined): { view: TurnedDownView; lit: ReadonlySet<string> } {
  const [pushed, setPushed] = useState<ReleasesTurnedDownEvent[]>([]);
  const [lit, light] = useLit();
  useLiveEvent("ReleasesTurnedDown", useCallback((event: ReleasesTurnedDownEvent) => {
    setPushed((current) => [...current, event].slice(-LATEST_LIMIT * 2));
    light(event.items.map((item) => item.group as TurnedDownGroup));
  }, [light]));
  const view = useMemo(() => mergeTurnedDown(extras?.turnedDown, pushed, extras?.asOfUtc), [extras?.turnedDown, extras?.asOfUtc, pushed]);
  return { view, lit };
}

/** The connections, with the newest pushed counts laid over them, and the ones that were just used. */
export function useConnections(extras: DashboardLiveExtras | null | undefined): { connections: LiveConnection[] | null; lit: ReadonlySet<string> } {
  const [used, setUsed] = useState<ReadonlyMap<string, ConnectionUsedEvent>>(() => new Map());
  const [lit, light] = useLit();
  useLiveEvent("ConnectionUsed", useCallback((event: ConnectionUsedEvent) => {
    setUsed((current) => new Map(current).set(connectionKey(event), event));
    light([connectionKey(event)]);
  }, [light]));
  const base = extras?.connections;
  const connections = useMemo(() => (base ? sortConnections(applyConnectionEvents(base, used, extras?.asOfUtc)) : null), [base, used, extras?.asOfUtc]);
  return { connections, lit };
}
