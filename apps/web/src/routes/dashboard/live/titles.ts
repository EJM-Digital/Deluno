/**
 * Which library title a card on the Live board is (#1096): the film with its
 * year, or the show with its season and episode, and its poster. The board asks
 * the library for every title on it in one request per kind of media; a title
 * that no longer exists is simply absent and its card keeps the release's own
 * name and its lane icon.
 */
import { fetchJson } from "../../../lib/api";
import { episodeFromRelease, type JourneyCard } from "./journey";

export interface TitleInfo {
  title?: string | null;
  year?: number | null;
  posterUrl?: string | null;
}

export interface EpisodeInfo {
  seriesId: string;
  season: number;
  episode: number;
}

export interface TitleLookup {
  movies: ReadonlyMap<string, TitleInfo>;
  series: ReadonlyMap<string, TitleInfo>;
  episodes: ReadonlyMap<string, EpisodeInfo>;
  /** Cards no sent download names (a download Deluno did not send itself), matched to the library by the release's own name: card key to title. */
  matched?: ReadonlyMap<string, Entity>;
  /** Which kinds the library has answered for: an id missing from an answered kind is a title that no longer exists. */
  answered?: { movies: boolean; series: boolean };
}

/** True when the library has answered and the title an entity names is not in it any more. */
export function isGone(entity: Entity | undefined, lookup: TitleLookup): boolean {
  if (!entity || !lookup.answered) return false;
  if (entity.kind === "movie") return lookup.answered.movies && !lookup.movies.get(entity.id)?.title;
  if (!lookup.answered.series) return false;
  if (entity.kind === "series") return !lookup.series.get(entity.id)?.title;
  const episode = lookup.episodes.get(entity.id);
  return !episode || !lookup.series.get(episode.seriesId)?.title;
}

export const NO_TITLES: TitleLookup = { movies: new Map(), series: new Map(), episodes: new Map() };

/** What a card shows: its title as a person knows it, its poster, and where it goes. */
export interface CardFace {
  title: string;
  poster?: string;
  /** True when the library named it: the release name is then a quiet second line. */
  linked: boolean;
  to: string;
}

const code = (season: number, episode: number) => `S${String(season).padStart(2, "0")}E${String(episode).padStart(2, "0")}`;

export type Entity = NonNullable<JourneyCard["entity"]>;

/** What an activity event or a download says it is about ("episode", "movie", "series"), as an entity; anything else is not a title. */
export function entityFromRelated(type: string | null | undefined, id: string | null | undefined): Entity | undefined {
  if (!id) return undefined;
  const kind = (type ?? "").toLowerCase();
  if (kind.includes("episode")) return { kind: "episode", id };
  if (kind.includes("movie")) return { kind: "movie", id };
  if (kind.includes("series") || kind.includes("show")) return { kind: "series", id };
  return undefined;
}

/** A title the library named: its name, the season and episode when it is one, its poster and its page. */
export interface NamedTitle {
  /** "Dune Part Two (2024)", "Lioness S03E08"; for a show alone, the show. */
  title: string;
  /** The film's or show's own name, without the year or episode. */
  name: string;
  /** "S03E08", when it is one episode. */
  code?: string;
  poster?: string;
  to: string;
}

export function namedTitle(entity: Entity | undefined, lookup: TitleLookup, release?: string | null): NamedTitle | null {
  if (!entity) return null;
  if (entity.kind === "movie") {
    const movie = lookup.movies.get(entity.id);
    if (!movie?.title) return null;
    return { title: movie.year ? `${movie.title} (${movie.year})` : movie.title, name: movie.title, poster: movie.posterUrl ?? undefined, to: `/movies/${entity.id}` };
  }
  if (entity.kind === "series") {
    const show = lookup.series.get(entity.id);
    if (!show?.title) return null;
    const which = release ? episodeFromRelease(release) : null;
    return { title: which ? `${show.title} ${which}` : show.title, name: show.title, code: which ?? undefined, poster: show.posterUrl ?? undefined, to: `/tv/${entity.id}` };
  }
  const episode = lookup.episodes.get(entity.id);
  const show = episode ? lookup.series.get(episode.seriesId) : undefined;
  if (!episode || !show?.title) return null;
  return { title: `${show.title} ${code(episode.season, episode.episode)}`, name: show.title, code: code(episode.season, episode.episode), poster: show.posterUrl ?? undefined, to: `/tv/${episode.seriesId}` };
}

export function faceOf(card: JourneyCard, lookup: TitleLookup): CardFace {
  const entity = card.entity ?? lookup.matched?.get(card.key);
  const named = namedTitle(entity, lookup, card.release);
  if (named) return { title: named.title, poster: named.poster, linked: true, to: named.to };
  // The poster alone (an older server) still makes the card; the name stays the release's.
  const poster = entity?.kind === "movie" ? lookup.movies.get(entity.id)?.posterUrl : entity?.kind === "series" ? lookup.series.get(entity.id)?.posterUrl : undefined;
  return { title: card.title, poster: poster ?? undefined, linked: false, to: card.to };
}

/** The ids a set of cards, and any other things that name titles, need asked of the library. */
export function wantedTitles(cards: JourneyCard[], extra: ReadonlyArray<Entity | undefined> = []): { movies: string[]; series: string[]; episodes: string[] } {
  const movies = new Set<string>();
  const series = new Set<string>();
  const episodes = new Set<string>();
  for (const entity of [...cards.map((card) => card.entity), ...extra]) {
    if (!entity) continue;
    (entity.kind === "movie" ? movies : entity.kind === "series" ? series : episodes).add(entity.id);
  }
  // The server answers at most 48 of each in one request.
  return { movies: [...movies].sort().slice(0, 48), series: [...series].sort().slice(0, 48), episodes: [...episodes].sort().slice(0, 48) };
}

/** What to look for in the library for a card that no sent download names: the release read as a film (with its year) or a show. */
export interface NameSearch {
  kind: "movie" | "series";
  name: string;
  year?: number;
}

export function nameSearchOf(card: JourneyCard): NameSearch | null {
  if (card.entity || card.stage === "searching") return null;
  const title = card.title.trim();
  const episode = /^(.*?)\s+S\d{1,2}E\d{1,3}$/i.exec(title);
  if (episode?.[1].trim()) return { kind: "series", name: episode[1].trim() };
  const year = /^(.*?)\s+\(((?:19|20)\d{2})\)$/.exec(title);
  if (year?.[1].trim()) return { kind: "movie", name: year[1].trim(), year: Number(year[2]) };
  return null;
}

const squash = (value: string) => value.toLowerCase().replace(/&/g, "and").replace(/[^a-z0-9]+/g, "");

interface LibraryRow {
  id: string;
  title?: string | null;
  releaseYear?: number | null;
  startYear?: number | null;
  posterUrl?: string | null;
}

/** The library's answer to a search by name: the title whose name is exactly the release's (and whose year agrees, for a film). */
export function pickByName(search: NameSearch, rows: ReadonlyArray<LibraryRow>): { id: string; info: TitleInfo } | null {
  const wanted = squash(search.name);
  const row = rows.find((candidate) => squash(candidate.title ?? "") === wanted && (search.kind === "series" || !search.year || !candidate.releaseYear || candidate.releaseYear === search.year));
  return row ? { id: row.id, info: { title: row.title, year: search.kind === "movie" ? row.releaseYear : row.startYear, posterUrl: row.posterUrl } } : null;
}

/** Looks the name up in the library (read only). A name the library does not have, or a failed request, is simply no match. */
export async function fetchByName(search: NameSearch) {
  const path = search.kind === "movie" ? "/api/movies/page" : "/api/series/page";
  try {
    const page = await fetchJson<{ items?: LibraryRow[] }>(`${path}?search=${encodeURIComponent(search.name)}&pageSize=10&facets=false`);
    return pickByName(search, page.items ?? []);
  } catch {
    return null;
  }
}

interface SeriesTitlesAnswer {
  series?: Record<string, TitleInfo>;
  episodes?: Record<string, EpisodeInfo>;
}

export async function fetchMovieTitles(ids: string[]): Promise<Record<string, TitleInfo>> {
  try {
    return await fetchJson<Record<string, TitleInfo>>(`/api/movies/titles?ids=${encodeURIComponent(ids.join(","))}`);
  } catch {
    // An older server: posters only (and a title that is gone is simply absent).
    return toPosters(await fetchJson<Record<string, string | null>>(`/api/movies/posters?ids=${encodeURIComponent(ids.join(","))}`).catch(() => ({})));
  }
}

export async function fetchSeriesTitles(ids: string[], episodeIds: string[]): Promise<SeriesTitlesAnswer> {
  try {
    return await fetchJson<SeriesTitlesAnswer>(`/api/series/titles?ids=${encodeURIComponent(ids.join(","))}&episodeIds=${encodeURIComponent(episodeIds.join(","))}`);
  } catch {
    const posters = ids.length > 0 ? await fetchJson<Record<string, string | null>>(`/api/series/posters?ids=${encodeURIComponent(ids.join(","))}`).catch(() => ({})) : {};
    return { series: toPosters(posters), episodes: {} };
  }
}

function toPosters(posters: Record<string, string | null>): Record<string, TitleInfo> {
  return Object.fromEntries(Object.entries(posters).map(([id, posterUrl]) => [id, { posterUrl }]));
}
