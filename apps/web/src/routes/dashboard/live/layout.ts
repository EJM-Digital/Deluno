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

