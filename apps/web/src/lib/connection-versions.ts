import { useCallback, useEffect, useState } from "react";
import { fetchJson } from "./api";

/** The version a connection last reported, and the newer stable one when it is out (#1117). `GET /api/connections/versions`. */
export interface ConnectionVersion {
  kind: "indexer" | "downloadClient" | "processor" | "mediaServer" | "requests" | string;
  id: string;
  version: string;
  /** The newest stable release of the project, when Deluno has read it; null when it could not be read. */
  latestVersion: string | null;
  updateAvailable: boolean;
  /** The release page of `latestVersion`, only when an update is available. */
  releaseUrl: string | null;
}

export type ConnectionVersions = ReadonlyMap<string, ConnectionVersion>;

export function versionKey(kind: string, id: string): string {
  return `${kind}:${id}`;
}

/**
 * What every connection last reported as its version. Read once on mount; a screen that tests a connection reloads
 * it with `reload`. A server that does not answer is no versions at all: nothing is shown, and nothing is wrong.
 */
export function useConnectionVersions(): { versions: ConnectionVersions; reload: () => void } {
  const [versions, setVersions] = useState<ConnectionVersions>(() => new Map());
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let live = true;
    fetchJson<ConnectionVersion[]>("/api/connections/versions")
      .then((items) => {
        if (!live || !Array.isArray(items)) return;
        setVersions(new Map(items.map((item) => [versionKey(item.kind, item.id), item])));
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, [tick]);
  const reload = useCallback(() => setTick((value) => value + 1), []);
  return { versions, reload };
}
