/**
 * What the Dashboard reads (#1082, #1081): one summary request, plus a few
 * cheap stored lists the tabs need for rows the summary does not carry. Nothing
 * here reaches a download client, an indexer, a media server or a disk while
 * the screen waits.
 */
import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson,
  fetchPageItems,
  type ActivityEventItem,
  type DownloadDispatchItem,
  type DownloadThroughputWindow,
  type DashboardLiveExtras,
  type DashboardSummary,
  type PolicySetItem,
  type QualityProfileItem,
  type SeriesUpcomingEpisodeItem,
  type SetupProgressItem
} from "../../lib/api";
import { queryClient, defineSources, useRouteSources, type RouteSource } from "../../lib/route-cache";
import { buildSetupStatus, type SetupStatusModel } from "../../lib/setup-status";
import { sharedSources } from "../../lib/shared-sources";
import type { IndexerScoreboardToday } from "./live/indexers";

/** Set by a pushed health change (#1233): the next read of the Live extras skips the server's five-second copy. */
let nextExtrasFresh = false;

/** Read the Live extras again now, past the server's remembered copy: a health change was pushed. */
export function refreshLiveExtrasNow() {
  nextExtrasFresh = true;
  return queryClient.invalidateQueries({ queryKey: ["src", "dashboard-live-extras"], exact: true });
}

/** The Live extras for the person's own day; null from an older server that has none. */
export async function fetchLiveExtras(): Promise<DashboardLiveExtras | null> {
  const midnight = new Date();
  midnight.setHours(0, 0, 0, 0);
  const fresh = nextExtrasFresh;
  nextExtrasFresh = false;
  try {
    const extras = await fetchJson<DashboardLiveExtras>(`/api/dashboard/live-extras?sinceUtc=${encodeURIComponent(midnight.toISOString())}${fresh ? "&fresh=true" : ""}`);
    return Array.isArray(extras?.arrivals) ? extras : null;
  } catch {
    return null;
  }
}

export const SUMMARY_KEY = ["dashboard", "summary"] as const;

export const summaryQuery = {
  queryKey: SUMMARY_KEY,
  queryFn: () => fetchJson<DashboardSummary>("/api/dashboard/summary"),
  // The server pushes the changed sections (#1097); the page asks for the whole summary once, and again only after a reconnect.
  staleTime: 3_000,
  refetchOnMount: "always"
} as const;

export function useDashboardSummary() {
  // No timer: the server pushes the changed sections, and a reconnect reads the summary once.
  return useQuery(summaryQuery);
}

/** Ask for a fresh summary now (the realtime nudge, a reconnect, a Retry). */
export function refreshSummary() {
  return queryClient.invalidateQueries({ queryKey: SUMMARY_KEY });
}

const EMPTY_PROGRESS: SetupProgressItem = { lastCompletedStep: 0, isSkipped: false, isCompleted: false, updatedUtc: new Date(0).toISOString() };

function source<T>(key: string, label: string, fetch: () => Promise<T>, fallback: T): RouteSource<T> {
  return { key: ["src", key], label, fetch, fallback };
}

export interface SetupNeed {
  id: string;
  title: string;
  text: string;
  to: string;
  action: string;
}

/** What the setup checklist reads; the same lists Media Management and Find & Download already keep. */
export const setupSources = defineSources({
  libraries: { ...sharedSources.libraries, fallback: [] },
  indexers: { ...sharedSources.indexers, fallback: [] },
  downloadClients: { ...sharedSources.downloadClients, fallback: [] },
  settings: { ...sharedSources.settings, fallback: emptyPlatformSettingsSnapshot },
  policySets: source<PolicySetItem[]>("policy-sets", "Library profiles", () => fetchJson("/api/policy-sets"), []),
  qualityProfiles: source<QualityProfileItem[]>("quality-profiles", "Quality profiles", () => fetchJson("/api/quality-profiles"), []),
  setupProgress: source<SetupProgressItem>("setup-progress", "Setup progress", () => fetchJson("/api/setup/progress"), EMPTY_PROGRESS)
});

/** The stored lists the Live and System tabs draw from, kept warm for the bell too. Nothing here is counted: the count is the server's. */
export const storedLists = defineSources({
  telemetry: sharedSources.telemetry,
  handoffs: sharedSources.handoffs,
  dispatches: sharedSources.dispatches,
  indexers: sharedSources.indexers
});

/** What only the Now tab reads beyond the summary and the download queue. */
export const liveSources = defineSources({
  upcoming: source<SeriesUpcomingEpisodeItem[]>("dashboard-upcoming", "Airing soon", () => fetchJson("/api/series/upcoming?take=20&hours=168"), []),
  // The journey of each title (#1096): the sent downloads, the processor hand-offs and the indexers, all stored lists the other screens already keep.
  dispatches: { ...sharedSources.dispatches, fallback: [] },
  // Every card is matched to the title it was grabbed for. The shared list holds only the newest 60, and a download that has been stuck for days is older than that (or archived), so the board reads a longer list that includes the archived ones.
  boardDispatches: source<DownloadDispatchItem[]>("dashboard-board-dispatches", "Sent downloads for the board", () => fetchPageItems<DownloadDispatchItem>("/api/download-dispatches?pageSize=300&includeArchived=true"), []),
  handoffs: { ...sharedSources.handoffs, fallback: [] },
  indexers: { ...sharedSources.indexers, fallback: [] },
  // The speed over the last six hours: the readings the server stores, kept current by the pushed events.
  throughput: { ...source<DownloadThroughputWindow>("dashboard-throughput-6h", "Speed over six hours", () => fetchJson("/api/download-clients/throughput?hours=6"), { hours: 6, samples: [] }) },
  // What each indexer did today (queries, grabs, failures, when it last answered): a stored count, cached on the server for a minute.
  scoreboard: { ...source<IndexerScoreboardToday | null>("dashboard-indexers-today", "Indexers today", () => fetchJson("/api/indexers/scoreboard?days=1"), null) },
  // The newest Activity, for the Latest stream: read from the stored list, kept current by the pushed events. The summary carries only eight rows, most of them routine.
  latestActivity: { ...source<ActivityEventItem[]>("dashboard-latest-activity", "Latest activity", () => fetchPageItems<ActivityEventItem>("/api/activity?pageSize=80"), []) },
  // Download clients with their on/off switch (the summary lists only the enabled ones): a switched-off client is a grey light.
  clients: { ...sharedSources.downloadClients, fallback: [] },
  // Today's arrivals and upgrades, what was refused, requests in flight and what retention will give back: stored rows, cached on the server for five seconds.
  liveExtras: { ...source<DashboardLiveExtras | null>("dashboard-live-extras", "Today on Live", fetchLiveExtras, null) }
});

export interface SetupState {
  status: SetupStatusModel;
  progress: SetupProgressItem;
  /** What the checklist raises beyond its own steps, as Needs you rows. */
  needs: SetupNeed[];
  ready: boolean;
}

type SetupLists = Parameters<typeof buildSetupStatus>[0];

/** The setup checklist, and what it raises beyond its own steps as Needs you rows. One rule for the Dashboard and the sidebar. */
export function buildSetupState(lists: SetupLists): { status: SetupStatusModel; needs: SetupNeed[] } {
  const status = buildSetupStatus(lists);
  // The ladder carries its own steps; only what it has no step for is a row.
  // Its "connection health" item is left out too: every indexer and client that
  // is not answering already has its own row with Test again, so it would say
  // the same problem twice.
  const needs = status.attentionItems
    .filter((item) => !status.steps.some((step) => step.id === item.id))
    .filter((item) => item.id !== "connection-health")
    .map((item) => ({ id: item.id, title: item.title, text: item.text, to: item.href, action: item.action }));
  return { status, needs };
}

/** The setup needs from whatever the cache holds now (the sidebar reads this; nothing is fetched). */
export function setupNeedsFromCache(): SetupNeed[] {
  const read = <T,>(src: RouteSource<T>) => queryClient.getQueryData<T>(src.key) ?? src.fallback;
  return buildSetupState({
    libraries: read(setupSources.libraries),
    downloadClients: read(setupSources.downloadClients),
    indexers: read(setupSources.indexers),
    policySets: read(setupSources.policySets),
    qualityProfiles: read(setupSources.qualityProfiles),
    settings: read(setupSources.settings)
  } as SetupLists).needs;
}

export function useSetupState(): SetupState {
  const { data, ready } = useRouteSources(setupSources);
  return useMemo(() => {
    const { status, needs } = buildSetupState({
      libraries: data.libraries,
      downloadClients: data.downloadClients,
      indexers: data.indexers,
      policySets: data.policySets,
      qualityProfiles: data.qualityProfiles,
      settings: data.settings
    });
    return { status, progress: data.setupProgress, needs, ready };
  }, [data, ready]);
}
