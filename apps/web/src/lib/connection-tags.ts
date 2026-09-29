import { useCallback, useState } from "react";
import type { BulkTagMode } from "../hooks/use-bulk-edit";
import { authedFetch } from "./use-auth";

export type ConnectionTagKind = "indexer" | "downloadClient" | "notification";

/** Add, remove or replace tags on several connections in one request (#1044). */
export async function applyConnectionTags(kind: ConnectionTagKind, ids: string[], tags: string[], mode: BulkTagMode) {
  const response = await authedFetch("/api/tags/bulk-connections", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ kind, ids, tags, mode })
  });
  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as { errors?: Record<string, string[]>; title?: string } | null;
    const detail = problem?.errors ? Object.values(problem.errors).flat()[0] : problem?.title;
    throw new Error(detail || "Could not change the tags.");
  }
  return (await response.json()) as { updated: number };
}

/** Which rows are ticked on a list that can be multi-selected. */
export function useRowSelection() {
  const [active, setActive] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const toggleActive = useCallback(() => {
    setActive((current) => !current);
    setSelected(new Set());
  }, []);
  const toggle = useCallback((id: string) => {
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);
  const setAll = useCallback((ids: string[]) => setSelected(new Set(ids)), []);
  const clear = useCallback(() => setSelected(new Set()), []);
  return { active, selected, toggleActive, toggle, setAll, clear };
}
