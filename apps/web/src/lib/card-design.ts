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
   * TV is deliberately still `false`: its Continuing hue is unsettled, and James
   * asked that the shelves not move together — *"frozen on today's card"* until
   * TV is decided on its own terms. Flipping this is how TV adopts it, and
   * nothing about the movie card changes when it does.
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

  /** Which bars carry a lead word. Settled for movies as the subtitle bar only. */
  readonly leads: "none" | "subtitles" | "both";

  /**
   * What the unfilled part of a bar is.
   *
   * `missing` — Missing red, which is what the part you do not have *is*.
   * `neutral` — the idle grey. Not used: grey means "unmonitored" and nothing
   * else, so a grey track makes a monitored title holding nothing read as
   * unmonitored.
   */
  readonly track: "missing" | "neutral";

  /**
   * What the filled part is coloured by.
   *
   * `mixed` — the rung's colour, except a Missing title's held part, which is
   * green because what you hold is held regardless of the rung. Required once
   * the track is red: without it a Missing title's fill and track are the same
   * colour and the fraction vanishes.
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
  // Frozen until TV is settled on its own terms. See `bars` above.
  bars: false,
  ladder: ["missing", "downloading", "upgrade", "airing", "covered", "upcoming"],
  mediaBar: "episodes",
  fillMeans: "coverage",
  leads: "subtitles",
  track: "missing",
  fill: "mixed"
};

export const CARD_DESIGN: Record<MediaType, CardDesign> = {
  movie: MOVIES,
  show: SHOWS
};

export function cardDesign(type: MediaType): CardDesign {
  return CARD_DESIGN[type];
}
