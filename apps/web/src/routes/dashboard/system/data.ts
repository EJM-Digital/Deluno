/**
 * What the System tab reads (#1098): the overview (GET /api/system/overview, then pushed as SystemOverviewChanged), and the
 * stored lists the other screens already keep: the health checks, the indexers and clients, the connections, the newest
 * activity and the backups. Nothing here reaches a client, indexer or disk while the screen waits.
 */
import { useQuery } from "@tanstack/react-query";
import { useCallback } from "react";
import { fetchJson, type BackupItem, type BackupSettingsSnapshot, type HealthCheckListResponse } from "../../../lib/api";
import type { SystemOverview } from "../../../lib/api/types/system-overview";
import type { SystemDatabasesResponse, SystemLogEntry, SystemLogResponse } from "../../../lib/api/types/system-log";
import { defineSources, queryClient } from "../../../lib/route-cache";
import { RealtimeGroups, useLiveEvent, useSignalREvent } from "../../../lib/use-signalr";
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
    staleTime: 1_000
  });
  useLiveEvent("SystemOverviewChanged", useCallback((event: SystemOverview) => {
    if (event && event.instance) queryClient.setQueryData(OVERVIEW_KEY, event);
  }, []));
  return query.data ?? null;
}

export const LOG_KEY = ["system", "log"] as const;
export const DATABASES_KEY = ["system", "databases"] as const;
const LOG_KEPT = 200;

/**
 * Deluno's own log for today (GET /api/system/log, newest first), kept current by the pushed SystemLogEntry event.
 * Null while it has not been read, or when the server does not have it (an older one).
 */
export function useSystemLog(): SystemLogResponse | null {
  const query = useQuery({
    queryKey: LOG_KEY,
    queryFn: async (): Promise<SystemLogResponse | null> => {
      try {
        const log = await fetchJson<SystemLogResponse>(`/api/system/log?level=all&take=${LOG_KEPT}`);
        return log && Array.isArray(log.entries) ? log : null;
      } catch {
        return null;
      }
    },
    staleTime: 5_000
  });
  useLiveEvent("SystemLogEntry", useCallback((entry: SystemLogEntry) => {
    if (!entry || typeof entry.id !== "string") return;
    queryClient.setQueryData<SystemLogResponse | null>(LOG_KEY, (current) => {
      if (!current || current.entries.some((row) => row.id === entry.id)) return current ?? null;
      return {
        ...current,
        entries: [entry, ...current.entries].slice(0, LOG_KEPT),
        errorsToday: current.errorsToday + (entry.level === "error" ? 1 : 0),
        warningsToday: current.warningsToday + (entry.level === "warning" ? 1 : 0)
      };
    });
  }, []));
  return query.data ?? null;
}

/** Each database's size and when it was last checked and compacted (GET /api/system/databases); null until read. */
export function useSystemDatabases(): SystemDatabasesResponse | null {
  const query = useQuery({
    queryKey: DATABASES_KEY,
    queryFn: async (): Promise<SystemDatabasesResponse | null> => {
      try {
        const body = await fetchJson<SystemDatabasesResponse>("/api/system/databases");
        return body && Array.isArray(body.databases) ? body : null;
      } catch {
        return null;
      }
    },
    staleTime: 30_000
  });
  // The sizes and the dates of the last check change on the server's say-so (#1179): no timer asks.
  useSignalREvent("SystemDatabasesChanged", RealtimeGroups.Dashboard, useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: DATABASES_KEY, exact: true });
  }, []));
  return query.data ?? null;
}
