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

/** What a station with nothing in it is given, in rem: its dot, its name and "none", so the rail stays readable. */
export const EMPTY_STATION_REM = 9.5;
/** The narrowest a full card and a compact row draw in, in rem, so a station knows how many columns its width allows. */
export const FULL_CARD_MIN_REM = 14;
export const COMPACT_ROW_MIN_REM = 10.5;
/** The height of one compact row with its gap, in rem. */
export const COMPACT_ROW_REM = 2.5;

/**
 * The width of each station, in px, out of `total`: stations with nothing shrink to a narrow column, and the busy ones share
 * the rest by how many cards they hold (never narrower than a station may be). With nothing anywhere they are equal.
 */
export function stationWidths(total: number, counts: readonly number[], remPx: number): number[] {
  const n = counts.length;
  if (!(total > 0) || n === 0) return counts.map(() => 0);
  const busy = counts.filter((count) => count > 0).length;
  const empty = EMPTY_STATION_REM * remPx;
  const min = MIN_STATION_REM * remPx;
  if (busy === 0 || busy === n || total - (n - busy) * empty < busy * min) return counts.map(() => total / n);
  const pool = total - (n - busy) * empty;
  const weight = counts.reduce((sum, count) => sum + Math.max(0, count), 0);
  const extra = pool - busy * min;
  return counts.map((count) => (count > 0 ? min + (extra * count) / weight : empty));
}

export interface StationLayout {
  density: "full" | "compact";
  /** Columns of cards the station's width allows and its cards need. */
  columns: number;
}

/**
 * How a station draws its cards in `width` x `height`: full cards, in as many columns as fit, while they all fit; otherwise
 * compact rows in as many columns as fit before it scrolls. Stacked stations are one column of full cards.
 */
export function stationLayout(count: number, width: number, height: number, cardHeight: number, remPx: number, mode: "columns" | "stacked"): StationLayout {
  if (mode === "stacked" || !(height > 0) || !(width > 0)) return { density: "full", columns: 1 };
  const gap = CARD_GAP_REM * remPx;
  const across = (min: number) => Math.max(1, Math.floor((width + gap) / (min * remPx + gap)));
  const fullRows = rowsThatFit(height, cardHeight, gap);
  const fullColumns = across(FULL_CARD_MIN_REM);
  if (count <= fullRows * fullColumns) return { density: "full", columns: Math.max(1, Math.min(fullColumns, Math.ceil(count / fullRows))) };
  const compactRows = rowsThatFit(height, COMPACT_ROW_REM * remPx, 0);
  return { density: "compact", columns: Math.max(1, Math.min(across(COMPACT_ROW_MIN_REM), Math.ceil(count / compactRows))) };
}

/**
 * The journey draws every station with ONE card form (full cards, or compact rows) so the board never mixes sizes: full
 * cards when every station's cards fit as full cards in its columns and height, otherwise compact rows everywhere. Widths
 * and columns are still each station's own. `widths` are the cards' widths per station, in px.
 */
export function boardLayouts(counts: readonly number[], widths: readonly number[], height: number, cardHeight: number, remPx: number, mode: "columns" | "stacked"): StationLayout[] {
  const layouts = counts.map((count, index) => stationLayout(count, widths[index] ?? 0, height, cardHeight, remPx, mode));
  if (layouts.every((layout) => layout.density === "full")) return layouts;
  const gap = CARD_GAP_REM * remPx;
  const compactRows = rowsThatFit(height, COMPACT_ROW_REM * remPx, 0);
  return counts.map((count, index) => {
    const across = Math.max(1, Math.floor(((widths[index] ?? 0) + gap) / (COMPACT_ROW_MIN_REM * remPx + gap)));
    return { density: "compact", columns: Math.max(1, Math.min(across, Math.ceil(count / compactRows))) };
  });
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

/** The largest a poster's picture may be, in rem (a lone poster on a big shelf is tall, not absurd). */
export const MAX_POSTER_REM = 16;

/**
 * Posters keep their 2:3 shape and fill the shelf. Given how many there are (`count`), it picks the fewest rows that show
 * them all, which is the largest poster that does: one row of tall posters when there are few. If they cannot all fit even
 * at the smallest poster, it takes the row count that shows the most, then the larger poster. Without a count it aims for
 * a poster about 7.5 rem wide. `caption` is the title under each, `gap` the space between.
 */
export function posterLayout(width: number, height: number, remPx: number, count?: number, gap = 0.875 * remPx): PosterLayout {
  const caption = 2.25 * remPx;
  const minPoster = 4.4 * remPx;
  const maxPoster = MAX_POSTER_REM * remPx;
  if (!(width > 0) || !(height > 0)) return { rows: 1, perRow: 1, width: minPoster, height: minPoster * 1.5, itemHeight: minPoster * 1.5 + caption, capacity: 1 };
  const shape = (rows: number) => {
    const rowHeight = (height - (rows - 1) * gap) / rows;
    // One row on a very short shelf may go below the usual smallest poster rather than be cut off.
    const pictureWidth = Math.min(maxPoster, Math.max(rows === 1 ? 2.5 * remPx : minPoster, Math.floor(((rowHeight - caption) * 2) / 3)));
    const fits = rows === 1 || rows * (pictureWidth * 1.5 + caption) + (rows - 1) * gap <= height;
    const perRow = Math.max(1, Math.floor((width + gap) / (pictureWidth + gap)));
    return { rows, pictureWidth, fits, perRow };
  };
  let best = shape(1);
  if (count !== undefined && count > 0) {
    // The widest poster that shows every title: each row count is limited by the height and by the width it needs.
    let widest: ReturnType<typeof shape> | undefined;
    let most = best;
    for (let rows = 1; rows <= 12; rows += 1) {
      const candidate = shape(rows);
      if (!candidate.fits) break;
      if (candidate.rows * candidate.perRow > most.rows * most.perRow) most = candidate;
      const across = Math.ceil(count / rows);
      const pictureWidth = Math.min(candidate.pictureWidth, Math.floor((width + gap) / across - gap));
      if (pictureWidth < (rows === 1 ? 2.5 * remPx : minPoster)) continue;
      if (!widest || pictureWidth > widest.pictureWidth) widest = { ...candidate, pictureWidth, perRow: across };
    }
    best = widest ?? most;
  } else {
    const target = (7.5 * 1.5 * remPx) + caption + gap;
    for (let rows = Math.max(1, Math.round((height + gap) / target)); rows >= 1; rows -= 1) {
      best = shape(rows);
      if (best.fits) break;
    }
  }
  const { rows, pictureWidth, perRow } = best;
  return { rows, perRow, width: pictureWidth, height: Math.round(pictureWidth * 1.5), itemHeight: Math.round(pictureWidth * 1.5) + caption, capacity: rows * perRow };
}
