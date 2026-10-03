import { useQuery } from "@tanstack/react-query";
import { fetchJson } from "./api";
import type { RouteSource } from "./route-cache";

/**
 * A connection with a newer stable version out (#1118). `GET /api/connections/updates`.
 *
 * `notice` is true while the bell should show it: a newer version is out and the person has not dismissed it (it
 * comes back for an even newer one, and goes the moment a check reads the new version). `mode` says what Deluno
 * can do: `updateNow` (Update now), `notChecked` (a build Deluno has not checked yet: release notes only) or
 * `manual` (release notes and a one-line how-to). `message` is the plain sentence for the screen.
 */
export interface ConnectionUpdate {
  kind: string;
  id: string;
  name: string;
  project: string;
  appName: string;
  version: string;
  latestVersion: string;
  releaseUrl: string | null;
  dismissedVersion: string | null;
  notice: boolean;
  mode: "updateNow" | "notChecked" | "manual" | string;
  targetVersion: string | null;
  message: string;
}

/** What Update now answers: `updated`, `unchanged` or `failed`, with the sentence to show. */
export interface ConnectionUpdateResult {
  outcome: "updated" | "unchanged" | "failed" | string;
  message: string;
  version: string | null;
}

export const connectionUpdatesSource: RouteSource<ConnectionUpdate[]> = {
  key: ["src", "connection-updates"],
  label: "Newer versions of connected apps",
  fetch: () => fetchJson<ConnectionUpdate[]>("/api/connections/updates")
};

/** "qBittorrent 5.0.2 is out · you have 4.6.7": the one line a notice says. */
export function updateNoticeTitle(item: Pick<ConnectionUpdate, "appName" | "latestVersion" | "version">): string {
  return `${item.appName} ${item.latestVersion} is out · you have ${item.version}`;
}

/** Where a connection is, so a notice opens that app's own connection. */
export function connectionLink(kind: string, id: string): string {
  const query = `?connection=${encodeURIComponent(id)}`;
  switch (kind) {
    case "downloadClient":
      return `/indexers/download-clients${query}`;
    case "processor":
      return `/settings/processing${query}`;
    case "mediaServer":
    case "requests":
      return `/indexers/media-servers${query}`;
    default:
      return "/system";
  }
}

/** The path that dismisses the notice for the newest version Deluno knows of on one connection. */
export function dismissPath(kind: string, id: string): string {
  return `/api/connections/${encodeURIComponent(kind)}/${encodeURIComponent(id)}/version/dismiss`;
}

/** The notices read through the shared cache, so an action that invalidates everything refreshes them too. */
export function useConnectionUpdates(): ConnectionUpdate[] {
  const { data } = useQuery({
    queryKey: connectionUpdatesSource.key,
    queryFn: connectionUpdatesSource.fetch,
    staleTime: 30_000
  });
  return Array.isArray(data) ? data : [];
}
