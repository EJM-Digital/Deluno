import { useCallback, useMemo, useState } from "react";

export interface ColumnDefinition {
  key: string;
  label: string;
  /** Whether a first-time visitor sees this column. */
  defaultVisible: boolean;
}

/**
 * Which optional columns a list table shows, remembered for the next visit
 * (#796, #798). Every page that grows a column picker was going to need the
 * same three things — a persisted choice, a safe fallback when the stored
 * value predates a column that did not exist yet, and a toggle — so it is one
 * hook rather than one copy per screen.
 *
 * A column absent from `defaultVisible` and never toggled by the person stays
 * out; adding a new optional column later does not retroactively turn it on
 * for someone who already chose a set (#936: nothing switches on by itself).
 */
export function useColumnVisibility(storageKey: string, columns: readonly ColumnDefinition[]) {
  const [visible, setVisible] = useState<Set<string>>(() => readStored(storageKey, columns));

  const toggle = useCallback(
    (key: string) => {
      setVisible((current) => {
        const next = new Set(current);
        if (next.has(key)) next.delete(key);
        else next.add(key);
        writeStored(storageKey, next);
        return next;
      });
    },
    [storageKey]
  );

  const isVisible = useCallback((key: string) => visible.has(key), [visible]);

  return useMemo(() => ({ visible, isVisible, toggle, columns }), [visible, isVisible, toggle, columns]);
}

function readStored(storageKey: string, columns: readonly ColumnDefinition[]): Set<string> {
  const fallback = new Set(columns.filter((column) => column.defaultVisible).map((column) => column.key));
  try {
    const raw = window.localStorage.getItem(storageKey);
    if (!raw) return fallback;
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return fallback;
    const known = new Set(columns.map((column) => column.key));
    return new Set(parsed.filter((value): value is string => typeof value === "string" && known.has(value)));
  } catch {
    return fallback;
  }
}

function writeStored(storageKey: string, visible: Set<string>) {
  try {
    window.localStorage.setItem(storageKey, JSON.stringify([...visible]));
  } catch {
    // A private window or a full quota loses the preference for next time,
    // not the ability to toggle it now.
  }
}
