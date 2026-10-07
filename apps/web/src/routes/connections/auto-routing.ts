import type { LibraryItem, LibraryRoutingSnapshot } from "../../lib/api";

/** What PUT /api/libraries/{id}/routing takes: the whole routing, replaced. */
export interface RoutingBody {
  sources: { indexerId: string; priority: number; requiredTags: string; excludedTags: string }[];
  downloadClients: { downloadClientId: string; priority: number; category: string | null }[];
}

export interface RoutingPlan {
  libraryId: string;
  libraryName: string;
  body: RoutingBody;
}

function bodyOf(snapshot: LibraryRoutingSnapshot | undefined): RoutingBody {
  return {
    sources: (snapshot?.sources ?? []).map((link) => ({ indexerId: link.indexerId, priority: link.priority, requiredTags: link.requiredTags ?? "", excludedTags: link.excludedTags ?? "" })),
    downloadClients: (snapshot?.downloadClients ?? []).map((link) => ({ downloadClientId: link.downloadClientId, priority: link.priority, category: link.category ?? null }))
  };
}

/** Whether a library of this type is one an indexer for this scope searches for. */
function scopeCovers(scope: string | null | undefined, mediaType: string): boolean {
  const wanted = scope ?? "both";
  return wanted === "both" || wanted === mediaType;
}

/**
 * Adding an indexer for Movies, TV or both switches it on for the libraries of
 * those types (#1135), so the answer given in the drawer is not asked again in
 * Library Routing. A library that already searches it is left alone.
 */
export function planIndexerRouting(
  indexer: { id: string; mediaScope?: string | null },
  libraries: Pick<LibraryItem, "id" | "name" | "mediaType">[],
  routing: LibraryRoutingSnapshot[]
): RoutingPlan[] {
  const plans: RoutingPlan[] = [];
  for (const library of libraries) {
    if (!scopeCovers(indexer.mediaScope, library.mediaType)) continue;
    const snapshot = routing.find((item) => item.libraryId === library.id);
    if (snapshot?.sources.some((link) => link.indexerId === indexer.id)) continue;
    const body = bodyOf(snapshot);
    body.sources.push({ indexerId: indexer.id, priority: body.sources.length + 1, requiredTags: "", excludedTags: "" });
    plans.push({ libraryId: library.id, libraryName: library.name, body });
  }
  return plans;
}

/** The library types a client's categories say it serves: both when it has none (Transmission has no categories). */
function typesServedBy(client: { moviesCategory?: string | null; tvCategory?: string | null; categoryTemplate?: string | null }): { movies: boolean; tv: boolean } {
  const movies = Boolean(client.moviesCategory?.trim() || client.categoryTemplate?.trim());
  const tv = Boolean(client.tvCategory?.trim());
  return movies || tv ? { movies, tv } : { movies: true, tv: true };
}

/**
 * The first download client connected for a library type is routed to that
 * library (#1135). A library that already has a client is left alone: a second
 * client is a choice the person makes in Library Routing.
 */
export function planClientRouting(
  client: { id: string; moviesCategory?: string | null; tvCategory?: string | null; categoryTemplate?: string | null },
  libraries: Pick<LibraryItem, "id" | "name" | "mediaType">[],
  routing: LibraryRoutingSnapshot[]
): RoutingPlan[] {
  const served = typesServedBy(client);
  const plans: RoutingPlan[] = [];
  for (const library of libraries) {
    if (!(library.mediaType === "tv" ? served.tv : served.movies)) continue;
    const snapshot = routing.find((item) => item.libraryId === library.id);
    if ((snapshot?.downloadClients.length ?? 0) > 0) continue;
    const body = bodyOf(snapshot);
    body.downloadClients.push({ downloadClientId: client.id, priority: 1, category: null });
    plans.push({ libraryId: library.id, libraryName: library.name, body });
  }
  return plans;
}
