/**
 * Discover's lists and filters (#1429). The filters are TMDb's own discover fields; the server turns them into the
 * metadata broker's ask. The genre ids are TMDb's published ones, which do not change.
 */

export type DiscoverKind = "movies" | "series";
export type DiscoverListName = "popular" | "trending" | "top-rated" | "upcoming" | "now-playing";

export interface DiscoverFilters {
  release: "" | "released" | "upcoming";
  yearFrom: string;
  yearTo: string;
  genres: number[];
  ratingMin: string;
  language: string;
  sort: "" | "rating" | "newest" | "oldest";
}

export const NO_FILTERS: DiscoverFilters = { release: "", yearFrom: "", yearTo: "", genres: [], ratingMin: "", language: "", sort: "" };

/** The recommendation lists TMDb offers, for films and for shows (it has no upcoming list for shows). */
export function discoverLists(kind: DiscoverKind): Array<{ value: DiscoverListName; label: string; title: string }> {
  const lists: Array<{ value: DiscoverListName; label: string; title: string }> = [
    { value: "popular", label: "Popular", title: "Popular on TMDb" },
    { value: "trending", label: "Trending", title: "Trending this week on TMDb" },
    { value: "top-rated", label: "Top rated", title: "Top rated on TMDb" }
  ];
  if (kind === "movies") {
    lists.push({ value: "upcoming", label: "Upcoming", title: "Upcoming films on TMDb" });
    lists.push({ value: "now-playing", label: "In cinemas", title: "In cinemas now, on TMDb" });
  } else {
    lists.push({ value: "now-playing", label: "On air", title: "On air now, on TMDb" });
  }
  return lists;
}

export const FILTERED_TITLE = "Titles that match your filters";

export const MOVIE_GENRES: ReadonlyArray<{ id: number; name: string }> = [
  { id: 28, name: "Action" }, { id: 12, name: "Adventure" }, { id: 16, name: "Animation" }, { id: 35, name: "Comedy" },
  { id: 80, name: "Crime" }, { id: 99, name: "Documentary" }, { id: 18, name: "Drama" }, { id: 10751, name: "Family" },
  { id: 14, name: "Fantasy" }, { id: 36, name: "History" }, { id: 27, name: "Horror" }, { id: 10402, name: "Music" },
  { id: 9648, name: "Mystery" }, { id: 10749, name: "Romance" }, { id: 878, name: "Science Fiction" },
  { id: 53, name: "Thriller" }, { id: 10752, name: "War" }, { id: 37, name: "Western" }
];

export const TV_GENRES: ReadonlyArray<{ id: number; name: string }> = [
  { id: 10759, name: "Action & Adventure" }, { id: 16, name: "Animation" }, { id: 35, name: "Comedy" }, { id: 80, name: "Crime" },
  { id: 99, name: "Documentary" }, { id: 18, name: "Drama" }, { id: 10751, name: "Family" }, { id: 10762, name: "Kids" },
  { id: 9648, name: "Mystery" }, { id: 10764, name: "Reality" }, { id: 10765, name: "Sci-Fi & Fantasy" },
  { id: 10766, name: "Soap" }, { id: 10768, name: "War & Politics" }, { id: 37, name: "Western" }
];

export function genresFor(kind: DiscoverKind): ReadonlyArray<{ id: number; name: string }> {
  return kind === "series" ? TV_GENRES : MOVIE_GENRES;
}

/** The original languages offered, as TMDb codes. */
export const LANGUAGES: ReadonlyArray<{ value: string; label: string }> = [
  { value: "en", label: "English" }, { value: "fr", label: "French" }, { value: "de", label: "German" },
  { value: "es", label: "Spanish" }, { value: "it", label: "Italian" }, { value: "pt", label: "Portuguese" },
  { value: "ja", label: "Japanese" }, { value: "ko", label: "Korean" }, { value: "zh", label: "Chinese" },
  { value: "hi", label: "Hindi" }, { value: "ru", label: "Russian" }, { value: "sv", label: "Swedish" },
  { value: "da", label: "Danish" }, { value: "nl", label: "Dutch" }
];

export const RATING_STEPS = ["5", "6", "7", "8"] as const;

/** How many filters are narrowing the grid. A year range counts once. */
export function activeFilterCount(filters: DiscoverFilters): number {
  return [
    filters.release !== "",
    filters.yearFrom.trim() !== "" || filters.yearTo.trim() !== "",
    filters.genres.length > 0,
    filters.ratingMin !== "",
    filters.language !== "",
    filters.sort !== ""
  ].filter(Boolean).length;
}

/** The query the Discover route reads. A blank year or a year that is not a number is left out. */
export function discoverQuery(kind: DiscoverKind, list: DiscoverListName, filters: DiscoverFilters, page: number): string {
  const params = new URLSearchParams({ mediaType: kind });
  if (page > 1) params.set("page", String(page));
  if (activeFilterCount(filters) === 0) {
    if (list !== "popular") params.set("list", list);
    return params.toString();
  }

  if (filters.release) params.set("release", filters.release);
  const from = Number.parseInt(filters.yearFrom, 10);
  const to = Number.parseInt(filters.yearTo, 10);
  if (Number.isFinite(from)) params.set("yearFrom", String(from));
  if (Number.isFinite(to)) params.set("yearTo", String(to));
  if (filters.genres.length > 0) params.set("genres", filters.genres.join(","));
  if (filters.ratingMin) params.set("ratingMin", filters.ratingMin);
  if (filters.language) params.set("language", filters.language);
  if (filters.sort) params.set("sort", filters.sort);
  return params.toString();
}

/** What the filters say in a sentence, for the line under the grid's title. */
export function describeFilters(kind: DiscoverKind, filters: DiscoverFilters): string {
  const parts: string[] = [];
  if (filters.release === "released") parts.push("already released");
  if (filters.release === "upcoming") parts.push("not out yet");
  const from = filters.yearFrom.trim();
  const to = filters.yearTo.trim();
  if (from && to) parts.push(`${from} to ${to}`);
  else if (from) parts.push(`from ${from}`);
  else if (to) parts.push(`up to ${to}`);
  if (filters.genres.length > 0) {
    const names = genresFor(kind).filter((genre) => filters.genres.includes(genre.id)).map((genre) => genre.name);
    parts.push(names.join(" and "));
  }
  if (filters.ratingMin) parts.push(`rated ${filters.ratingMin}+`);
  if (filters.language) parts.push(`in ${LANGUAGES.find((language) => language.value === filters.language)?.label ?? filters.language}`);
  return parts.join(", ");
}
