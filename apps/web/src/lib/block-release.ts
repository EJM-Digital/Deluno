/**
 * Blocking a release from wherever it is named (cleanup sheet J5, #925).
 *
 * The Queue already asks "why remove this" and, for "It's bad", stops the
 * download, deletes its files, blocklists the release everywhere and starts
 * a replacement search — see removal-reason-dialog.tsx and
 * POST /api/download-clients/{clientId}/queue/{queueItemId}/remove (#924).
 * This module is for every other place a release is named — a search
 * result never grabbed, a title's search history, an Activity entry — where
 * the release may or may not still be sitting in a client's queue.
 *
 * `findQueueMatch` answers that "may or may not" by reading the same
 * telemetry overview the Queue screen shows, matched by hash first (a
 * torrent's infohash or an NZB's item id, whichever indexer offered it this
 * time — #874) and by release name otherwise, since a release not yet
 * downloaded has no hash to match on.
 */
import { fetchJson } from "./api/client";
import { authedFetch } from "./use-auth";
import type { BlockedRelease, DownloadTelemetryOverview } from "./api/types";

export interface ReleaseIdentity {
  releaseName: string;
  indexerName?: string | null;
  /** A torrent's infohash or a Usenet client's item id, when known (#874). */
  hash?: string | null;
  mediaType?: string | null;
  entityId?: string | null;
  /** The title this release belongs to, for plain-language confirms. */
  title?: string | null;
}

export interface QueueMatch {
  clientId: string;
  clientName: string;
  queueItemId: string;
}

/** Reads a queue snapshot already fetched for the match a release identity carries. */
export function findQueueMatch(
  overview: DownloadTelemetryOverview,
  identity: ReleaseIdentity
): QueueMatch | null {
  const hash = identity.hash?.trim().toLowerCase() || null;
  const name = identity.releaseName.trim().toLowerCase();

  for (const client of overview.clients) {
    for (const item of client.queue) {
      const itemHash = item.id?.trim().toLowerCase();
      const itemName = item.releaseName?.trim().toLowerCase();
      const hashMatches = Boolean(hash) && itemHash === hash;
      const nameMatches = itemName === name;
      if (hashMatches || nameMatches) {
        return { clientId: client.clientId, clientName: client.clientName, queueItemId: item.id };
      }
    }
  }

  return null;
}

/**
 * Whether this release is downloading right now, so the confirm can offer
 * to stop it. Best-effort: a telemetry read that fails just means no match
 * is offered, never that blocking itself is blocked by it.
 */
export async function checkQueueMatch(identity: ReleaseIdentity): Promise<QueueMatch | null> {
  try {
    const overview = await fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry");
    return findQueueMatch(overview, identity);
  } catch {
    return null;
  }
}

/**
 * Whether this release is already refused, so "Block this release" can
 * become "Lift the block" wherever blocking is offered (#925) — search
 * results, the Queue, a title's history, and Activity — not only on the
 * Blocklist screen. Matched the same way as the queue: by hash first, then
 * by release name. A proposal (Deluno's own "ask me" case) is not a
 * decided block, so it does not count here. Best-effort, like the queue
 * check above: a read that fails just means no lift is offered.
 */
export async function checkBlockedMatch(identity: ReleaseIdentity): Promise<BlockedRelease | null> {
  try {
    const releases = await fetchJson<BlockedRelease[]>("/api/blocked-releases");
    const hash = identity.hash?.trim().toLowerCase() || null;
    const name = identity.releaseName.trim().toLowerCase();
    return (
      releases.find((release) => {
        if (release.state === "proposed") return false;
        const releaseHash = release.torrentHashOrItemId?.trim().toLowerCase();
        const releaseName = release.releaseName?.trim().toLowerCase();
        return (Boolean(hash) && releaseHash === hash) || releaseName === name;
      }) ?? null
    );
  } catch {
    return null;
  }
}

/** Lifts an existing block. Never starts a search — that is a person's own decision (DESIGN-007 decision 8). */
export async function liftBlock(id: string): Promise<Response> {
  return authedFetch(`/api/blocked-releases/${encodeURIComponent(id)}`, { method: "DELETE" });
}

/** Block only. Nothing is stopped or deleted; see stopBlockAndSearchAgain for that. */
export async function blockReleaseOnly(identity: ReleaseIdentity): Promise<{ response: Response; blocked: BlockedRelease | null }> {
  const response = await authedFetch("/api/blocked-releases", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      releaseName: identity.releaseName,
      indexerName: identity.indexerName ?? null,
      torrentHashOrItemId: identity.hash ?? null,
      mediaType: identity.mediaType ?? null,
      entityId: identity.entityId ?? null,
      title: identity.title ?? identity.releaseName
    })
  });

  const blocked = response.ok ? ((await response.json()) as BlockedRelease) : null;
  return { response, blocked };
}

/**
 * Reuses the Queue's "It's bad" path exactly (#924): removes the download
 * from its client, deletes its files, blocklists the release everywhere,
 * and starts a replacement search when Deluno can.
 */
export async function stopBlockAndSearchAgain(clientId: string, queueItemId: string): Promise<Response> {
  return authedFetch(`/api/download-clients/${encodeURIComponent(clientId)}/queue/${encodeURIComponent(queueItemId)}/remove`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ reason: "failed" })
  });
}
