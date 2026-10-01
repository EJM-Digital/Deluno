/**
 * The layout rules of Live (#1098), as plain functions of measured space. Nothing
 * on the page is locked to a pixel size: the journey asks how many card rows its
 * height holds, the shelf asks what poster size its box allows, and the page asks
 * whether the journey is wide enough for its stations side by side. The components
 * measure (see `useElementSize`); these decide.
 */

/** The narrowest a station may be, in rem, before the journey stacks its stations one under another. */
export const MIN_STATION_REM = 9;

/** The gap between cards and between tiles, in rem. */
export const CARD_GAP_REM = 0.375;

/** How many whole rows of `rowHeight` (with `gap` between) fit in `height`. Always at least one, so a station is never empty. */
export function rowsThatFit(height: number, rowHeight: number, gap: number): number {
  if (!(height > 0) || !(rowHeight > 0)) return 1;
  return Math.max(1, Math.floor((height + gap) / (rowHeight + gap)));
}

/** The journey keeps its stations side by side when each has room, and stacks them when it does not (a phone, a narrow window). */
export function journeyMode(width: number, stations: number, remPx: number): "columns" | "stacked" {
  if (!(width > 0)) return "columns";
  return width >= stations * MIN_STATION_REM * remPx ? "columns" : "stacked";
}

/**
 * How a station draws its cards: as full cards while they fit its height, as compact poster tiles when there are more
 * than fit, so every title stays on screen. Stacked stations flow down the page, which scrolls, so they are always full.
 */
export function stationDensity(count: number, height: number, cardHeight: number, gap: number, mode: "columns" | "stacked"): "full" | "compact" {
  if (mode === "stacked" || !(height > 0)) return "full";
  return count <= rowsThatFit(height, cardHeight, gap) ? "full" : "compact";
}

/** The main area must be at least this wide (in rem) for the right column to sit beside the journey. */
export const SIDE_BY_SIDE_REM = 71.875;
/** Below this main-area width (in rem) the top band stacks its three cards. */
export const BAND_ACROSS_REM = 56.25;
/** The journey's share of the height of the space under the top band (the rest goes to Arrived and Turned down today). */
export const JOURNEY_SHARE = 0.512;

export interface PageLayout {
  /** The right column sits beside the page (full height) rather than below it. */
  sideBySide: boolean;
  /** The top band keeps its three cards across, or stacks them. */
  band: "across" | "stacked";
}

/** The page decides from the width of its own main area, not the window: unmeasured (0) is the roomy layout. */
export function pageLayout(width: number, remPx: number): PageLayout {
  if (!(width > 0)) return { sideBySide: true, band: "across" };
  return { sideBySide: width >= SIDE_BY_SIDE_REM * remPx, band: width >= BAND_ACROSS_REM * remPx ? "across" : "stacked" };
}

/**
 * The tallest the journey may be: its share of the space under the top band when the page is held to the window, or
 * most of a window's height when the page scrolls. It is a ceiling, not a size: the journey is as tall as its cards need.
 */
export function journeyBudget(sideBySide: boolean, restHeight: number, windowHeight: number): number | undefined {
  if (sideBySide) return restHeight > 0 ? Math.floor(restHeight * JOURNEY_SHARE) : undefined;
  return windowHeight > 0 ? Math.floor(windowHeight * 0.7) : undefined;
}

/** The height the journey's cards may use: its budget less the header, stations and spacing around them (chrome). */
export function cardsBudget(budget: number | undefined, chrome: number): number | undefined {
  return budget === undefined ? undefined : Math.max(0, budget - chrome);
}

export interface PosterLayout {
  /** Rows of posters the shelf holds. */
  rows: number;
  /** Posters across. */
  perRow: number;
  /** Width and height of one poster's picture (2:3). */
  width: number;
  height: number;
  /** The height of one poster with its caption. */
  itemHeight: number;
  /** How many posters the shelf shows. */
  capacity: number;
}

/**
 * Posters keep their 2:3 shape and size themselves to the shelf: one row on a short shelf, more rows on a tall screen, and
 * a poster is never smaller or larger than reads well. `captionHeight` is the title under each, `gap` the space between.
 */
export function posterLayout(width: number, height: number, remPx: number, gap = 0.875 * remPx): PosterLayout {
  const caption = 2.25 * remPx;
  const minPoster = 4.4 * remPx;
  const maxPoster = 10 * remPx;
  if (!(width > 0) || !(height > 0)) return { rows: 1, perRow: 1, width: minPoster, height: minPoster * 1.5, itemHeight: minPoster * 1.5 + caption, capacity: 1 };
  // A row is a poster, its caption and the gap; the target is a poster about 7.5 rem wide.
  const target = (7.5 * 1.5 * remPx) + caption + gap;
  let rows = Math.max(1, Math.round((height + gap) / target));
  let pictureWidth = minPoster;
  for (; rows >= 1; rows -= 1) {
    const rowHeight = (height - (rows - 1) * gap) / rows;
    // One row on a very short shelf may go below the usual smallest poster rather than be cut off.
    pictureWidth = Math.min(maxPoster, Math.max(rows === 1 ? 2.5 * remPx : minPoster, Math.floor(((rowHeight - caption) * 2) / 3)));
    // The rows must really fit: the smallest poster can be taller than a share of a short shelf.
    if (rows === 1 || rows * (pictureWidth * 1.5 + caption) + (rows - 1) * gap <= height) break;
  }
  const perRow = Math.max(1, Math.floor((width + gap) / (pictureWidth + gap)));
  return { rows, perRow, width: pictureWidth, height: Math.round(pictureWidth * 1.5), itemHeight: Math.round(pictureWidth * 1.5) + caption, capacity: rows * perRow };
}


/** The narrowest a tile may be in the right column, in rem. */
export const MIN_TILE_REM = 7;

const TILE_GAP_REM = 0.375;
const TILE_PAD_X_REM = 1.125;

/** Tiles go three across, as in the mockup, however wide the column is. */
export const MAX_TILE_COLUMNS = 3;

/** How many tiles go across a card of `width` px. At least one. */
export function tileColumns(width: number, remPx: number): number {
  if (!(width > 0)) return 3;
  const inner = width - 2 * TILE_PAD_X_REM * remPx;
  return Math.min(MAX_TILE_COLUMNS, Math.max(1, Math.floor((inner + TILE_GAP_REM * remPx) / ((MIN_TILE_REM + TILE_GAP_REM) * remPx))));
}

/** The room a tile card takes, in rem: its header, its padding and its rows of tiles. A card with no tiles is one short line. */
export function tileCardRem(count: number, cols: number, compact: boolean): number {
  const header = 3.2;
  if (count === 0) return header + 2.6;
  const rows = Math.ceil(count / Math.max(1, cols));
  const tile = compact ? 1.45 : 3.1;
  const gap = compact ? 0.25 : TILE_GAP_REM;
  return header + (compact ? 0.5 : 1.25) + rows * tile + (rows - 1) * gap;
}

/** The least room Activity is given, in rem: its header and about three rows. */
export const ACTIVITY_MIN_REM = 16.5;

/**
 * The three tile cards draw full tiles (name and a line of detail) while they and Activity's least room fit the column's height,
 * and one-line tiles (a light and the name) before anything is clipped. A column with no measured height draws full tiles.
 */
export function tileDensity(height: number, counts: readonly number[], cols: number, remPx: number): "full" | "compact" {
  if (!(height > 0)) return "full";
  const gap = (counts.length) * 0.5;
  const need = counts.reduce((sum, count) => sum + tileCardRem(count, cols, false), 0) + gap + ACTIVITY_MIN_REM;
  return need * remPx <= height ? "full" : "compact";
}
