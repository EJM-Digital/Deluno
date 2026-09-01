import type { MediaType } from "./media-types";
import type { TitleMark } from "./status-tones";

/**
 * How each shelf's card differs, declared once per medium.
 *
 * James: *"they should be independant of each other, tv and movie"*, and
 * separately *"same look, separate declarations"* — each shelf declares its own
 * choices so either can change without touching the other, even where the two
 * currently agree.
 *
 * **Independent configuration, shared mechanism.** Everything that differs by
 * medium is a value in this table. Everything that does not — the two-layer
 * clipped label, the contrast rules, the unmonitored override — lives once in
 * `title-mark.tsx`. Two copies of *that* is the shape of every defect this
 * project keeps finding, including three found while designing this very card:
 * a label painted twice, an override applied to a fill but not its track, and a
 * fill colour on a zero-width element.
 *
 * The spine of the difference, from DESIGN-006 §2: **a film is one file and a
 * show is many.** A film has a single quality to name and no partial coverage of
 * itself; a show has no single quality — twenty episodes can sit at twenty tiers
 * — and its fraction is how much of what aired is on disk. That is why Radarr
 * prints `Remux-2160p` where Sonarr prints `16 / 16`, and it is not a preference.
 */
export interface CardDesign {
  /**
   * Whether this shelf draws the card settled in DESIGN-006.
   *
   * Movies and TV opt in independently. TV now uses the shared mechanism with
   * its own episode-coverage bar, red Missing remainder, held/quality fill,
   * and Continuing hue; changing this declaration does not change the movie
   * shelf.
   */
  readonly bars: boolean;

  /** The rungs a title on this shelf can be on. A film is never still airing. */
  readonly ladder: readonly TitleMark[];

  /** What the top bar spells out, when its switch is on. */
  readonly mediaBar: "quality" | "episodes";

  /**
   * What the top bar's fill measures.
   *
   * A film is not partway through itself, so its fill is free to mean the one
   * fraction a film actually has — how far the download has got. A show's is
   * coverage of what has aired.
   */
  readonly fillMeans: "download" | "coverage";

  /** Which bars carry a lead word. Settled for both shelves as the subtitle bar only. */
  readonly leads: "none" | "subtitles" | "both";

  /**
   * What the unfilled part of a bar is.
   *
   * `missing` — Missing red, which is what the part you do not have *is*.
   * `neutral` — the idle grey remainder, with the state colour carried by the
   * filled portion. It is retained for renderer comparisons; the product
   * shelves use `missing` so the part not held is visibly Missing.
   */
  readonly track: "missing" | "neutral";

  /**
   * What the filled part is coloured by.
   *
   * `mixed` — the rung's colour, except a Missing title's held part, which is
   * green because what you hold is held regardless of the rung. Required when
   * the track is red: without it a Missing title's fill and track are the same
   * colour and the fraction vanishes.
   *
   * `held` — the held portion is green, except a fully held Quality met title,
   * which is gold. This is the active TV composition and keeps the same
   * meaning as the subtitle bar: colour says what that segment is.
   *
   * `state` — the rung's colour is retained in the held portion. This is kept
   * for renderer comparisons and exploratory designs.
   *
   * Inert on the movie shelf — measured, zero movie cards render differently
   * across the three rules, because a film is held or it is not. It is declared
   * anyway so the two shelves stay separately readable.
   */
  readonly fill: "state" | "mixed" | "held";
}

const MOVIES: CardDesign = {
  bars: true,
  ladder: ["missing", "downloading", "upgrade", "covered", "upcoming"],
  mediaBar: "quality",
  fillMeans: "download",
  leads: "subtitles",
  track: "missing",
  fill: "mixed"
};

const SHOWS: CardDesign = {
  // TV now adopts DESIGN-006 independently of Movies. Its media bar measures
  // aired-episode coverage, and its Continuing rung has its own TV-only hue.
  bars: true,
  ladder: ["missing", "downloading", "upgrade", "airing", "covered", "upcoming"],
  mediaBar: "episodes",
  fillMeans: "coverage",
  leads: "subtitles",
  track: "missing",
  fill: "held"
};

export const CARD_DESIGN: Record<MediaType, CardDesign> = {
  movie: MOVIES,
  show: SHOWS
};

export function cardDesign(type: MediaType): CardDesign {
  return CARD_DESIGN[type];
}
