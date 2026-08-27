import type { SeriesEpisodeInventoryItem } from "./api";

/**
 * What a show's episodes add up to, counted over what has **aired**.
 *
 * Counting an episode that has not aired as missing blames the library for the
 * calendar. Slow Horses read "Find 36 missing episodes" and `MISSING 36` when
 * only 30 had aired — six of those could not be found by anyone, and offering to
 * go and look for them is offering to fail. The same mistake `DESIGN-001` forbids
 * the bar under a poster from making, on the page that shows the episodes
 * themselves.
 *
 * The rule lives here rather than in the two places that were counting, because
 * two copies of a rule is how they came to disagree with the bar in the first
 * place.
 */

/** An episode with no air date has not been scheduled, so it has not aired. */
export function hasAired(episode: Pick<SeriesEpisodeInventoryItem, "airDateUtc">, now: Date = new Date()): boolean {
  if (!episode.airDateUtc) return false;
  const aired = new Date(episode.airDateUtc);
  return !Number.isNaN(aired.getTime()) && aired.getTime() <= now.getTime();
}

/**
 * Missing means: it aired, and Deluno does not have it. An episode still to
 * come is `upcoming`, which is a different fact and gets its own count.
 */
export function isEpisodeMissing(
  episode: Pick<SeriesEpisodeInventoryItem, "airDateUtc" | "hasFile" | "wantedStatus">,
  now: Date = new Date()
): boolean {
  if (episode.hasFile) return false;
  return hasAired(episode, now);
}

/** Aired, not yet — the two halves that used to be one number. */
export function isEpisodeUpcoming(
  episode: Pick<SeriesEpisodeInventoryItem, "airDateUtc" | "hasFile">,
  now: Date = new Date()
): boolean {
  return !episode.hasFile && !hasAired(episode, now);
}

export interface EpisodeProgress {
  /** Everything the catalogue knows about, aired or not. */
  total: number;
  aired: number;
  /** Aired and held. This is the numerator the bar under a poster uses. */
  held: number;
  /** Aired and absent. Worth searching for, and worth reporting. */
  missing: number;
  /** Not out yet. Nothing to look for, so nothing to report as a shortfall. */
  upcoming: number;
  upgradable: number;
}

export function summariseEpisodes(
  episodes: readonly Pick<SeriesEpisodeInventoryItem, "airDateUtc" | "hasFile" | "wantedStatus">[],
  now: Date = new Date()
): EpisodeProgress {
  let aired = 0;
  let held = 0;
  let missing = 0;
  let upcoming = 0;
  let upgradable = 0;

  for (const episode of episodes) {
    if (hasAired(episode, now)) {
      aired++;
      if (episode.hasFile) held++;
      else missing++;
    } else if (!episode.hasFile) {
      upcoming++;
    }

    if (episode.hasFile && episode.wantedStatus === "upgrade") upgradable++;
  }

  return { total: episodes.length, aired, held, missing, upcoming, upgradable };
}
