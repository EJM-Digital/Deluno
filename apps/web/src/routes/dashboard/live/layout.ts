/**
 * The layout rules of Live (#1098), as plain functions of measured space. Nothing
 * on the page is locked to a pixel size: the journey asks how many card rows its
 * grid cell holds, the shelf asks what poster size its box allows, and the page asks
 * whether the journey is wide enough for its stations side by side. The components
 * measure (see `useElementSize`); these decide.
 */

/** The narrowest a station may be, in rem, before the journey stacks its stations one under another. */
export const MIN_STATION_REM = 10;

/** One row of the journey's lanes, as the mockup: a card is 86px tall and a row is 94px (8px between). */
export const TOKEN_PX = 86;
export const ROW_PX = 94;

/** The line "and N more" takes under the last card: a gap and one line of text, in px. */
export const MORE_PX = 22;

export interface LanePlan {
  /** Rows of cards the lanes draw. */
  rows: number;
  /** Some station has more titles than the rows, so each such station ends with "and N more". */
  more: boolean;
  /** So little room that the "and N more" line takes the last row itself, as in the mockup, instead of a line under the cards. */
  slot: boolean;
  /** The height of the lanes, in px. */
  height: number;
}

/**
 * How the lanes use `room` px for stations holding `counts` titles. All the cards that fit are drawn; when the busiest
 * station has more than fit, every station draws as many as fit and the busy ones end with an "and N more" line, which
 * takes less than a row (or, with room for no more than one row, the row itself). At least one row. Without a `room` (the page scrolls) every title is drawn.
 */
export function lanePlan(counts: readonly number[], room: number | undefined): LanePlan {
  const most = Math.max(1, ...counts);
  const rowsFor = (space: number) => Math.max(1, Math.floor((space + (ROW_PX - TOKEN_PX)) / ROW_PX));
  if (room === undefined || most <= rowsFor(room)) return { rows: most, more: false, slot: false, height: most * ROW_PX - (ROW_PX - TOKEN_PX) };
  if (room - MORE_PX < TOKEN_PX) return { rows: 1, more: true, slot: true, height: TOKEN_PX };
  const rows = rowsFor(room - MORE_PX);
  return { rows, more: true, slot: false, height: rows * ROW_PX - (ROW_PX - TOKEN_PX) + MORE_PX };
}

/** The journey keeps its stations side by side when each has room, and stacks them when it does not (a phone, a narrow window). */
export function journeyMode(width: number, stations: number, remPx: number): "columns" | "stacked" {
  if (!(width > 0)) return "columns";
  return width >= stations * MIN_STATION_REM * remPx ? "columns" : "stacked";
}

/** The main area must be at least this wide (in rem) for the right column to sit beside the journey. */
export const SIDE_BY_SIDE_REM = 71.875;
/** Below this main-area width (in rem) the top band stacks its three cards. */
export const BAND_ACROSS_REM = 56.25;
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
 * The one shared grid of the Live tab beside the right column (the mockup's `main`): the top band, the journey and the
 * bottom row are rows, Sources spans the band and the journey, Activity sits beside the bottom row, so the tops and
 * bottoms line up across both columns. Row heights are fluid within bounds, shares of the window's height (the page is fit to the window here).
 */
export const GRID_COLUMNS = "minmax(0, 1fr) clamp(340px, 28%, 600px)";
export const GRID_AREAS = '"now src" "board src" "low act"';
/** The bottom row's own two columns: Arrived today and Turned down. */
export const LOW_COLUMNS = "minmax(0, 1fr) clamp(260px, 36%, 420px)";

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

/**
 * The row heights, as shares of the window's height (`basis`), clamped as the mockup does. When the grid itself (`available`,
 * less what is above it, such as setup banners) is too short for both rows and a journey, they give way together so the
 * journey keeps at least 250px of it. Unmeasured (0) is a roomy window.
 */
export function gridRows(basis: number, available = 0): { band: number; low: number; template: string } {
  const h = basis > 0 ? basis : 1000;
  let band = clamp(h * 0.2, 150, 200);
  let low = clamp(h * 0.27, 180, 300);
  // The journey keeps 250px (its header, stations and one row of cards) and at least 35% of the grid.
  const room = available > 0 ? Math.max(available * 0.4, Math.min(available * 0.65, available - 28 - 250)) : 0;
  if (available > 0 && band + low > room) {
    const scale = room / (band + low);
    band *= scale;
    low *= scale;
  }
  band = Math.round(band);
  low = Math.round(low);
  return { band, low, template: `${band}px minmax(0, 1fr) ${low}px` };
}

/** Under the side-by-side threshold the page scrolls, and the journey may be at most most of a window tall. */
export function stackedJourneyBudget(windowHeight: number): number | undefined {
  return windowHeight > 0 ? Math.floor(windowHeight * 0.7) : undefined;
}

/** The height the journey's cards may use: its budget less the header, stations and spacing around them (chrome). */
export function cardsBudget(budget: number | undefined, chrome: number): number | undefined {
  return budget === undefined ? undefined : Math.max(0, budget - chrome);
}

export interface PosterLayout {
  /** Always one row: a second, partial row is never drawn. */
  rows: 1;
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

/** The widest a poster's picture may be (the mockup's 130px) and the narrowest (40px, so a very short shelf shows whole posters rather than cutting them), in px. */
export const MAX_POSTER_PX = 130;
export const MIN_POSTER_PX = 40;
/** The caption under a poster, and the space between posters, in px. */
export const POSTER_CAPTION_PX = 36;
export const POSTER_GAP_PX = 16;

/**
 * The mockup's poster rule: one row, a poster 2:3 sized from the height of the shelf (less its caption), at most 130px
 * wide and at least 64px: more posters across rather than bigger ones.
 */
export function posterLayout(width: number, height: number): PosterLayout {
  const wide = clamp(Math.round(((height - POSTER_CAPTION_PX) * 2) / 3), MIN_POSTER_PX, MAX_POSTER_PX);
  const perRow = width > 0 ? Math.max(1, Math.floor((width + POSTER_GAP_PX) / (wide + POSTER_GAP_PX))) : 1;
  return { rows: 1, perRow, width: wide, height: Math.round(wide * 1.5), itemHeight: Math.round(wide * 1.5) + POSTER_CAPTION_PX, capacity: perRow };
}
