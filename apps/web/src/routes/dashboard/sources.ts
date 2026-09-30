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
  type DownloadThroughputWindow,
  type DashboardSummary,
  type PolicySetItem,
  type QualityProfileItem,
  type SeriesUpcomingEpisodeItem,
  type SetupProgressItem
} from "../../lib/api";
import { queryClient, defineSources, useRouteSources, type RouteSource } from "../../lib/route-cache";
import { buildSetupStatus, type SetupStatusModel } from "../../lib/setup-status";
import { sharedSources } from "../../lib/shared-sources";
import type { SetupNeed } from "./attention";

/** What the newest page of Activity holds; when today's counts cannot be had, this is all the bars can show. */
export const ACTIVITY_PAGE_SIZE = 200;

export interface ActivityHourCount {
  hour: number;
  category: string;
  count: number;
}

export interface ActivityToday {
  /** When the counts were read here; events that arrive after this are added on top. */
  fetchedAt: number;
  /** The day's counts by the hour, or null when the server cannot give them (an older server). */
  counts: ActivityHourCount[] | null;
  /** The newest events, used only when there are no counts. */
  rows: ActivityEventItem[];
  /** True when `rows` stopped at the page size, so it is not the whole day. */
  capped: boolean;
}

/** Today's activity: real counts by the hour from the server, or, from an older server, the newest page and the fact that it is only that. */
export async function fetchActivityToday(): Promise<ActivityToday> {
  const fetchedAt = Date.now();
  const midnight = new Date(fetchedAt);
  midnight.setHours(0, 0, 0, 0);
  try {
    const result = await fetchJson<{ buckets: ActivityHourCount[] }>(
      `/api/activity/by-hour?sinceUtc=${encodeURIComponent(midnight.toISOString())}&offsetMinutes=${-midnight.getTimezoneOffset()}`
    );
    if (Array.isArray(result?.buckets)) return { fetchedAt, counts: result.buckets, rows: [], capped: false };
  } catch {
    // An older server has no such count; fall through to the newest page.
  }
  const rows = await fetchPageItems<ActivityEventItem>(`/api/activity?pageSize=${ACTIVITY_PAGE_SIZE}`);
  return { fetchedAt, counts: null, rows, capped: rows.length >= ACTIVITY_PAGE_SIZE };
}

export const SUMMARY_KEY = ["dashboard", "summary"] as const;

export const summaryQuery = {
  queryKey: SUMMARY_KEY,
  queryFn: () => fetchJson<DashboardSummary>("/api/dashboard/summary"),
  // Realtime nudges drive freshness; this is the safety net for a missed one.
  staleTime: 3_000,
  refetchInterval: 60_000,
  refetchOnMount: "always"
} as const;

export function useDashboardSummary() {
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

/** What only the Now tab reads beyond the summary and the download queue. */
export const liveSources = defineSources({
  upcoming: source<SeriesUpcomingEpisodeItem[]>("dashboard-upcoming", "Coming up", () => fetchJson("/api/series/upcoming?take=12&hours=72"), []),
  // The journey of each title (#1096): the sent downloads, the processor hand-offs and the indexers, all stored lists the other screens already keep.
  dispatches: { ...sharedSources.dispatches, fallback: [] },
  handoffs: { ...sharedSources.handoffs, fallback: [] },
  indexers: { ...sharedSources.indexers, fallback: [] },
  // The speed over the last six hours: the readings the server stores, refreshed behind realtime as a safety net.
  throughput: { ...source<DownloadThroughputWindow>("dashboard-throughput-6h", "Speed over six hours", () => fetchJson("/api/download-clients/throughput?hours=6"), { hours: 6, samples: [] }), refetchInterval: 60_000 },
  // Today's activity for the hourly bars: the day's real counts by the hour; live events are added as they arrive.
  // The newest Activity, for the Latest stream: read from the stored list, refreshed behind realtime as a safety net. The summary carries only eight rows, most of them routine.
  latestActivity: { ...source<ActivityEventItem[]>("dashboard-latest-activity", "Latest activity", () => fetchPageItems<ActivityEventItem>("/api/activity?pageSize=80"), []), refetchInterval: 120_000 },
  activityToday: { ...source<ActivityToday | null>("dashboard-activity-today", "Today's activity", fetchActivityToday, null), refetchInterval: 120_000 }
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
