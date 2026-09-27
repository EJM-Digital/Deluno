import type { MediaScope } from "./presets";

/** One standard Newznab/Torznab category this indexer's protocol can report (#751). */
export interface CategoryDefinition {
  id: string;
  name: string;
  group: "Movies" | "TV";
}

/**
 * The standard category ids INDEXER_PRESETS.defaultCategories already uses
 * (#751), named the way Newznab/Torznab servers themselves name them, so
 * ticking every box in a group reproduces exactly the same stored value the
 * "Use the standard categories" reset button already writes.
 */
export const STANDARD_CATEGORIES: readonly CategoryDefinition[] = [
  { id: "2000", name: "Movies", group: "Movies" },
  { id: "2010", name: "Foreign", group: "Movies" },
  { id: "2020", name: "Other", group: "Movies" },
  { id: "2030", name: "SD", group: "Movies" },
  { id: "2040", name: "HD", group: "Movies" },
  { id: "2045", name: "UHD", group: "Movies" },
  { id: "2050", name: "BluRay", group: "Movies" },
  { id: "2060", name: "3D", group: "Movies" },
  { id: "2070", name: "DVD", group: "Movies" },
  { id: "5000", name: "TV", group: "TV" },
  { id: "5010", name: "WEB-DL", group: "TV" },
  { id: "5020", name: "Foreign", group: "TV" },
  { id: "5030", name: "SD", group: "TV" },
  { id: "5040", name: "HD", group: "TV" },
  { id: "5045", name: "UHD", group: "TV" },
  { id: "5050", name: "Other", group: "TV" },
  { id: "5060", name: "Sport", group: "TV" },
  { id: "5070", name: "Anime", group: "TV" }
];

/** The comma-separated category ids field, trimmed and emptied of blanks, in stored order. */
export function splitCategoryIds(categories: string): string[] {
  return categories.split(",").map((id) => id.trim()).filter(Boolean);
}

/** The same list of ids, joined the way the stored field already writes them. */
export function joinCategoryIds(ids: string[]): string {
  return ids.join(",");
}

/** Whether `id` is already one of the comma-separated category ids. */
export function categoryIdSelected(categories: string, id: string): boolean {
  return splitCategoryIds(categories).includes(id);
}

/** Adds or removes `id` from a category ids field, leaving every other id (standard or custom) exactly where it was. */
export function toggleCategoryId(categories: string, id: string): string {
  const ids = splitCategoryIds(categories);
  const next = ids.includes(id) ? ids.filter((existing) => existing !== id) : [...ids, id];
  return joinCategoryIds(next);
}

/** The standard categories worth showing for this indexer's used-for scope: both groups for "both", one group otherwise. */
export function standardCategoriesForScope(scope: MediaScope): readonly CategoryDefinition[] {
  return STANDARD_CATEGORIES.filter((category) => scope === "both" || (scope === "movies" ? category.group === "Movies" : category.group === "TV"));
}

/**
 * Ids in the field that are not one of the known standard ones (#751): a
 * custom category the indexer itself reports, or a number nobody has typed a
 * name for yet. Shown in the picker labelled as custom rather than only as a
 * bare number, and still edited through the same free-text fallback.
 */
export function customCategoryIds(categories: string): string[] {
  const standardIds = new Set(STANDARD_CATEGORIES.map((category) => category.id));
  return splitCategoryIds(categories).filter((id) => !standardIds.has(id));
}
