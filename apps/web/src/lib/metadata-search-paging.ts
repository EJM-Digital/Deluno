import type { MetadataSearchResult } from "./api";

/**
 * One page of title matches: TMDb's own page of twenty, the same number the server asks for and returns (#1473).
 * Every screen that lists matches uses this, so none cuts a page short and none guesses where a page ends.
 */
export const METADATA_SEARCH_PAGE_SIZE = 20;

/** The page to ask for next after a page came back, or null when that page was short and there is nothing more. */
export function nextMetadataPage(page: number, received: number): number | null {
  return received >= METADATA_SEARCH_PAGE_SIZE ? page + 1 : null;
}

/** Adds a page to the matches already shown. TMDb's pages shift while people search, so a film it repeats is kept once. */
export function mergeMetadataPage(current: MetadataSearchResult[], incoming: MetadataSearchResult[]): MetadataSearchResult[] {
  const seen = new Set(current.map((item) => `${item.provider}:${item.providerId}`));
  return [...current, ...incoming.filter((item) => !seen.has(`${item.provider}:${item.providerId}`))];
}
