/**
 * A release a search held for review (#1159): usable, but not safe to send on its own, so a person says yes.
 * Transfers lists it under Needs action with the candidate and three answers.
 *
 * - Approve asks the server to judge the release again and send it (it refuses with a plain reason when the title
 *   or episode no longer wants it or today's rules refuse that release), then stops listing it.
 * - Find another refuses that release and searches again.
 * - Dismiss stops listing it and changes nothing else.
 */
import { fetchJson } from "./api/client";
import { blockReleaseOnly } from "./block-release";
import { authedFetch } from "./use-auth";

/** Mirrors `AttentionEndpoints.HeldReleaseRow`. */
export interface HeldReleaseRow {
  historyId: string;
  mediaType: "movie" | "series";
  id: string;
  episodeId: string | null;
  title: string;
  year: number | null;
  episodeLabel: string | null;
  libraryId: string;
  releaseName: string;
  indexerName: string | null;
  indexerId: string | null;
  downloadUrl: string | null;
  quality: string | null;
  sizeBytes: number | null;
  seeders: number | null;
  reason: string | null;
  heldUtc: string;
}

export const heldReleasesSource = {
  key: ["src", "attention-held-releases"],
  label: "Releases held for your approval",
  fetch: () => fetchJson<HeldReleaseRow[]>("/api/attention/held-releases"),
  fallback: [] as HeldReleaseRow[]
};

/** The name a row goes by: "Severance S01E02" for an episode, "Dune (2021)" for a film. */
export function heldReleaseTitle(row: HeldReleaseRow): string {
  if (row.mediaType === "series") return row.episodeLabel ? `${row.title} ${row.episodeLabel}` : row.title;
  return row.year ? `${row.title} (${row.year})` : row.title;
}

function collection(row: HeldReleaseRow): "movies" | "series" {
  return row.mediaType === "movie" ? "movies" : "series";
}

async function failure(response: Response, fallback: string): Promise<Error> {
  const body = (await response.json().catch(() => null)) as { message?: string; errors?: Record<string, string[]> } | null;
  const first = body?.errors ? Object.values(body.errors).flat()[0] : undefined;
  return new Error(body?.message || first || fallback);
}

export async function dismissHeldRelease(row: HeldReleaseRow): Promise<void> {
  const response = await authedFetch(`/api/attention/held-releases/${encodeURIComponent(row.historyId)}/dismiss`, { method: "POST" });
  if (!response.ok) throw await failure(response, "That could not be dismissed.");
}

/** The server judges the release again against today's rules, refuses with a plain reason (409) or sends it and answers the row. */
export async function approveHeldRelease(row: HeldReleaseRow): Promise<void> {
  const response = await authedFetch(`/api/attention/held-releases/${encodeURIComponent(row.historyId)}/approve`, { method: "POST" });
  if (!response.ok) throw await failure(response, `${row.releaseName} could not be sent to a download client.`);
}

export async function findAnotherForHeldRelease(row: HeldReleaseRow): Promise<void> {
  const { response } = await blockReleaseOnly({
    releaseName: row.releaseName,
    indexerName: row.indexerName,
    mediaType: row.mediaType === "movie" ? "movie" : "tv",
    entityId: row.id,
    title: heldReleaseTitle(row)
  });
  if (!response.ok) throw await failure(response, `${row.releaseName} could not be refused.`);
  const search = row.mediaType === "series" && row.episodeId
    ? await authedFetch(`/api/series/${encodeURIComponent(row.id)}/episodes/search`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ episodeIds: [row.episodeId], monitoredOnly: false })
      })
    : await authedFetch(`/api/${collection(row)}/${encodeURIComponent(row.id)}/search${row.libraryId ? `?libraryId=${encodeURIComponent(row.libraryId)}` : ""}`, { method: "POST" });
  if (!search.ok) throw await failure(search, "The release was refused, but the new search could not be started.");
  await dismissHeldRelease(row);
}
