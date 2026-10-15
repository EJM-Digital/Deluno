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

/** An episode with no air date has not aired, but it is Missing, not Upcoming (James, 30 Sep, #1072). */
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
  episode: Pick<SeriesEpisodeInventoryItem, "airDateUtc" | "hasFile" | "wantedStatus"> & { monitored?: boolean },
  now: Date = new Date()
): boolean {
  // An episode nobody wants (removed by retention or unmonitored) is not missing, only not wanted.
  if (episode.monitored === false || episode.hasFile) return false;
  return hasAired(episode, now) || !episode.airDateUtc;
}

/** Only an episode with a future air date is Upcoming; an undated one is Missing. */
export function isEpisodeUpcoming(
  episode: Pick<SeriesEpisodeInventoryItem, "airDateUtc" | "hasFile"> & { monitored?: boolean },
  now: Date = new Date()
): boolean {
  return episode.monitored !== false && !episode.hasFile && !!episode.airDateUtc && !hasAired(episode, now);
}

/** Upgradable means: Deluno wants it, it holds a file, and that file is below the target. */
export function isEpisodeUpgradable(
  episode: Pick<SeriesEpisodeInventoryItem, "hasFile" | "wantedStatus"> & { monitored?: boolean }
): boolean {
  return episode.monitored !== false && episode.hasFile && episode.wantedStatus === "upgrade";
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
  episodes: readonly (Pick<SeriesEpisodeInventoryItem, "airDateUtc" | "hasFile" | "wantedStatus"> & { monitored?: boolean })[],
  now: Date = new Date()
): EpisodeProgress {
  let aired = 0;
  let held = 0;
  let missing = 0;
  let upcoming = 0;
  let upgradable = 0;

  for (const episode of episodes) {
    // Counted over the episodes Deluno wants: an unmonitored one is not missing, upcoming, held or upgradable.
    if (episode.monitored === false) continue;

    if (hasAired(episode, now)) {
      aired++;
      if (episode.hasFile) held++;
      else missing++;
    } else if (!episode.hasFile) {
      if (episode.airDateUtc) upcoming++;
      else missing++;
    }

    if (isEpisodeUpgradable(episode)) upgradable++;
  }

  return { total: episodes.length, aired, held, missing, upcoming, upgradable };
}
