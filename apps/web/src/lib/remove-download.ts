/**
 * The Queue's three-way "why remove this" action (cleanup sheet J1/J2,
 * #924), shared by every place a download still sitting in a client can be
 * removed — the Queue itself (queue-screen.tsx), a title's dispatch history
 * and search history (movie-detail-page.tsx, show-detail-page.tsx), and
 * Activity's "Sent to downloads" list (download-dispatch-drawer.tsx). One
 * call to POST /api/download-clients/{clientId}/queue/{queueItemId}/remove
 * does the client action, the blocklist entry and the replacement search;
 * "I don't want it" additionally unmonitors the title, which happens on the
 * movie or series side rather than here — Deluno's engines stay separate, so
 * that step is a second, best-effort call through the same bulk endpoint
 * the title's own screen uses.
 */
import { fetchJson, readErrorText } from "./api/client";
import { authedFetch } from "./use-auth";
import type { QueueRemovalResult } from "./api/types";
import type { RemovalReason } from "../components/ui/removal-reason-dialog";

/**
 * Where "I don't want it" unmonitors the title, and what it sends — a pure
 * mapping so the choice can be tested without a render (#924). Movie and TV
 * engines stay separate, so this picks between their two, otherwise
 * identical, bulk endpoints rather than one shared one.
 */
export function unmonitorRequestFor(entityId: string, mediaType: string | null): { url: string; body: Record<string, unknown> } {
  const isTv = mediaType === "tv" || mediaType === "series";
  return isTv
    ? { url: "/api/series/bulk", body: { seriesIds: [entityId], operation: "monitoring", monitored: false } }
    : { url: "/api/movies/bulk", body: { movieIds: [entityId], operation: "monitoring", monitored: false } };
}

/** Posts the chosen reason and returns the result, or throws with the server's own message. */
export async function submitQueueRemoval(clientId: string, queueItemId: string, reason: RemovalReason): Promise<QueueRemovalResult> {
  const response = await authedFetch(
    `/api/download-clients/${clientId}/queue/${encodeURIComponent(queueItemId)}/remove`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ reason })
    }
  );
  const result = (await response.json().catch(() => null)) as QueueRemovalResult | null;
  if (!response.ok || !result?.succeeded) {
    throw new Error(result?.message || (await readErrorText(response, "That removal could not be completed.")));
  }
  return result;
}

/**
 * Best-effort: the download is already gone and its files deleted, which is
 * the part that matters. A title left monitored is visible and easy to fix
 * by hand; silently failing the whole removal over it would not be.
 */
export async function unmonitorTitleAfterRemoval(entityId: string, mediaType: string | null): Promise<void> {
  const { url, body } = unmonitorRequestFor(entityId, mediaType);
  try {
    await fetchJson(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
  } catch {
    // See comment above.
  }
}
