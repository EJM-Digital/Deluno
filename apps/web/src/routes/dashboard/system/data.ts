/**
 * What the System tab reads (#1098): the overview (GET /api/system/overview, then pushed as SystemOverviewChanged), and the
 * stored lists the other screens already keep: the health checks, the indexers and clients, the connections, the newest
 * activity and the backups. Nothing here reaches a client, indexer or disk while the screen waits.
 */
import { useQuery } from "@tanstack/react-query";
import { useCallback } from "react";
import { fetchJson, type BackupItem, type BackupSettingsSnapshot, type HealthCheckListResponse } from "../../../lib/api";
import type { SystemOverview } from "../../../lib/api/types/system-overview";
import { usePollInterval } from "../../../lib/realtime-state";
import { defineSources, queryClient } from "../../../lib/route-cache";
import { useLiveEvent } from "../../../lib/use-signalr";
import { liveSources } from "../sources";

export const systemSources = defineSources({
  // The stored result of every check, the same list System → Health reads.
  healthChecks: { key: ["src", "system-health"], label: "Health checks", fetch: () => fetchJson<HealthCheckListResponse>("/api/system/health") },
  // The Live reads the tab shares: the connections, the newest activity, the indexers and clients with their switches.
  liveExtras: liveSources.liveExtras,
  latestActivity: liveSources.latestActivity,
  indexers: liveSources.indexers,
  clients: liveSources.clients,
  backups: { key: ["src", "backups"], label: "Backups", fetch: () => fetchJson<BackupItem[]>("/api/backups"), fallback: [] as BackupItem[] },
  backupSettings: { key: ["src", "backup-settings"], label: "Backup settings", fetch: () => fetchJson<BackupSettingsSnapshot>("/api/backups/settings") }
});

export const OVERVIEW_KEY = ["system", "overview"] as const;

/** The machine's readings, kept current by the pushed event; null while there is none (an older server, or not read yet). */
export function useSystemOverview(): SystemOverview | null {
  const query = useQuery({
    queryKey: OVERVIEW_KEY,
    queryFn: async (): Promise<SystemOverview | null> => {
      try {
        const overview = await fetchJson<SystemOverview>("/api/system/overview");
        return overview && typeof overview === "object" && overview.instance ? overview : null;
      } catch {
        return null;
      }
    },
    staleTime: 1_000,
    // A safety net for while the connection is down; with it up the server pushes a reading every 2 seconds.
    refetchInterval: usePollInterval(10_000)
  });
  useLiveEvent("SystemOverviewChanged", useCallback((event: SystemOverview) => {
    if (event && event.instance) queryClient.setQueryData(OVERVIEW_KEY, event);
  }, []));
  return query.data ?? null;
}
