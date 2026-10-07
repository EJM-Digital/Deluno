import { useEffect, useMemo, useState } from "react";
import { fetchJson, type LibraryItem, type LibraryRoutingSnapshot } from "../../lib/api";
import type { FolderChainReport } from "./folder-chain-section";

export type RoutingStatusTone = "ok" | "warn" | "info" | "idle";
export interface RoutingStatus {
  tone: RoutingStatusTone;
  label: string;
  /** One plain sentence saying what is wrong, when something is. */
  hint?: string;
  /** True when the row should offer a Fix button that opens the library's routing. */
  needsFix?: boolean;
}

/** What the folder check said about a library's routed clients: undefined while it has not answered. */
export type RoutingCheck = { state: "checking" } | { state: "done"; problem: string | null; unknown: boolean };

/**
 * The first thing the folder check found wrong with a routed client's category, label or folders (#1133),
 * in the check's own words. Null when every client link matches.
 */
export function clientProblemOf(report: Pick<FolderChainReport, "clients">): { problem: string | null; unknown: boolean } {
  let unknown = false;
  for (const client of report.clients) {
    for (const link of client.links) {
      if (link.status === "problem") return { problem: `${client.clientName}: ${link.message}`, unknown };
      if (link.status === "unknown") unknown = true;
    }
  }
  return { problem: null, unknown };
}

/**
 * The status a library's row in Library Routing shows. A library whose routed client lacks the category,
 * or whose Label plugin is off, is not Ready (#1133): the same check the drawer's folder check runs decides.
 */
export function routingStatus(snapshot: LibraryRoutingSnapshot | undefined, check?: RoutingCheck): RoutingStatus {
  const sources = snapshot?.sources ?? [];
  const targets = snapshot?.downloadClients ?? [];
  if (!sources.length) return { tone: "warn", label: "No indexers" };
  if (!targets.length) return { tone: "warn", label: "No client" };
  if (check?.state === "done" && check.problem) {
    return { tone: "warn", label: "Needs a fix", hint: check.problem, needsFix: true };
  }
  if (check?.state === "checking") return { tone: "idle", label: "Checking" };
  if (check?.state === "done" && check.unknown) {
    return { tone: "info", label: "Not fully checked", hint: "A download client did not say where it saves. Open this library to check again." };
  }
  return targets.some((target) => Boolean(target.category?.trim()))
    ? { tone: "info", label: "Category route" }
    : { tone: "ok", label: "Ready" };
}

/**
 * Runs the folder check for each library that has an indexer and a client, in the background, so the list
 * can say "Needs a fix" instead of "Ready" (#1133). Re-runs when a library's routed clients change.
 */
export function useRoutingChecks(
  enabled: boolean,
  libraries: Pick<LibraryItem, "id">[],
  routing: LibraryRoutingSnapshot[]
): Record<string, RoutingCheck> {
  const [checks, setChecks] = useState<Record<string, RoutingCheck>>({});
  const targets = useMemo(
    () => libraries
      .map((library) => ({ id: library.id, snapshot: routing.find((item) => item.libraryId === library.id) }))
      .filter((item) => (item.snapshot?.sources.length ?? 0) > 0 && (item.snapshot?.downloadClients.length ?? 0) > 0)
      .map((item) => `${item.id}:${item.snapshot!.downloadClients.map((link) => `${link.downloadClientId}/${link.category ?? ""}`).join(",")}`),
    [libraries, routing]
  );
  const key = targets.join("|");

  useEffect(() => {
    if (!enabled || !key) return;
    let active = true;
    const controller = new AbortController();
    for (const target of key.split("|")) {
      const id = target.slice(0, target.indexOf(":"));
      setChecks((current) => ({ ...current, [id]: { state: "checking" } }));
      fetchJson<FolderChainReport>(`/api/libraries/${id}/folder-chain`, { signal: controller.signal })
        .then((report) => { if (active) setChecks((current) => ({ ...current, [id]: { state: "done", ...clientProblemOf(report) } })); })
        .catch(() => { if (active) setChecks((current) => ({ ...current, [id]: { state: "done", problem: null, unknown: true } })); });
    }
    return () => { active = false; controller.abort(); };
  }, [enabled, key]);

  return checks;
}
