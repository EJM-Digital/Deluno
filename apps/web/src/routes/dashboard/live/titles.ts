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

export function faceOf(card: JourneyCard, lookup: TitleLookup): CardFace {
  const entity = card.entity;
  if (entity?.kind === "movie") {
    const movie = lookup.movies.get(entity.id);
    if (movie?.title) return { title: movie.year ? `${movie.title} (${movie.year})` : movie.title, poster: movie.posterUrl ?? undefined, linked: true, to: `/movies/${entity.id}` };
  } else if (entity?.kind === "series") {
    const show = lookup.series.get(entity.id);
    if (show?.title) {
      const which = card.release ? episodeFromRelease(card.release) : null;
      return { title: which ? `${show.title} ${which}` : show.title, poster: show.posterUrl ?? undefined, linked: true, to: `/tv/${entity.id}` };
    }
  } else if (entity?.kind === "episode") {
    const episode = lookup.episodes.get(entity.id);
    const show = episode ? lookup.series.get(episode.seriesId) : undefined;
    if (episode && show?.title) return { title: `${show.title} ${code(episode.season, episode.episode)}`, poster: show.posterUrl ?? undefined, linked: true, to: `/tv/${episode.seriesId}` };
  }
  // The poster alone (an older server) still makes the card; the name stays the release's.
  const poster = entity?.kind === "movie" ? lookup.movies.get(entity.id)?.posterUrl : entity?.kind === "series" ? lookup.series.get(entity.id)?.posterUrl : undefined;
  return { title: card.title, poster: poster ?? undefined, linked: false, to: card.to };
}

/** The ids a set of cards needs asked of the library. */
export function wantedTitles(cards: JourneyCard[]): { movies: string[]; series: string[]; episodes: string[] } {
  const movies = new Set<string>();
  const series = new Set<string>();
  const episodes = new Set<string>();
  for (const card of cards) {
    if (!card.entity) continue;
    (card.entity.kind === "movie" ? movies : card.entity.kind === "series" ? series : episodes).add(card.entity.id);
  }
  return { movies: [...movies].sort().slice(0, 48), series: [...series].sort().slice(0, 48), episodes: [...episodes].sort().slice(0, 48) };
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
