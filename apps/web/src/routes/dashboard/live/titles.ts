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
  const named = namedTitle(card.entity, lookup, card.release);
  if (named) return { title: named.title, poster: named.poster, linked: true, to: named.to };
  const entity = card.entity;
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
  return { movies: [...movies].sort().slice(0, 80), series: [...series].sort().slice(0, 80), episodes: [...episodes].sort().slice(0, 80) };
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
