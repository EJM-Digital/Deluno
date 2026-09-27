/**
 * The Queue's three-way "why remove this" action (cleanup sheet J1/J2,
 * #924), shared by every place a download still sitting in a client can be
 * removed — the Queue itself (queue-screen.tsx), a title's dispatch history
 * and search history (movie-detail-page.tsx, show-detail-page.tsx), and
 * Activity's "Sent to downloads" list (download-dispatch-drawer.tsx). One
 * call to POST /api/download-clients/{clientId}/queue/{queueItemId}/remove
 * does the client action, the blocklist entry, the replacement search, and —
 * for "I don't want it" — the unmonitor too, all on the server, so there is
 * nothing left for the caller to follow up with.
 */
import { readErrorText } from "./api/client";
import { authedFetch } from "./use-auth";
import type { QueueRemovalResult } from "./api/types";
import type { RemovalReason } from "../components/ui/removal-reason-dialog";

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
