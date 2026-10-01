/**
 * The layout rules of Live (#1098), as plain functions of measured space. Nothing
 * on the page is locked to a pixel size: the journey asks how many card rows its
 * height holds, the shelf asks what poster size its box allows, and the page asks
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
export function journeyBudget(sideBySide: boolean, restHeight: number, windowHeight: number, belowHeight = 0): number | undefined {
  // Beside the right column, whatever the content-sized row under the journey (Arrived today, Turned down) does not use is the journey's.
  if (sideBySide) return restHeight > 0 ? (belowHeight > 0 ? Math.max(0, Math.floor(restHeight - belowHeight)) : Math.floor(restHeight * JOURNEY_SHARE)) : undefined;
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

/** The largest a poster's picture may be, in rem (the mockup's poster: more across rather than bigger). */
export const MAX_POSTER_REM = 7.5;

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
