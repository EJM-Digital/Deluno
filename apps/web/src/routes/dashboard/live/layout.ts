/**
 * The layout rules of Live (#1098), as plain functions of measured space. Nothing
 * on the page is locked to a pixel size: the journey asks how many card rows its
 * grid cell holds, the shelf asks what poster size its box allows, and the page asks
 * whether the journey is wide enough for its stations side by side. The components
 * measure (see `useElementSize`); these decide.
 */

/** The narrowest a station may be, in rem, before the journey stacks its stations one under another. */
export const MIN_STATION_REM = 8;

/** One row of the journey's lanes at the mockup's full size: a card is 86px tall and a row is 94px (8px between). */
export const TOKEN_PX = 86;
export const ROW_PX = 94;
/** The gap between one row of cards and the next, in px. */
export const ROW_GAP_PX = 8;

/** The line "and N more" takes under the last row: a gap and one line of text, in px (always kept, as the mockup does). */
export const MORE_PX = 22;

/** The smallest a journey card gets (its row step is that plus the 8px gap): below this the band gives way before the cards do. */
export const MIN_TOKEN_PX = 60;
/** The tallest a row step grows to on a tall window: a 122px card (the mockup's 130px step). */
export const MAX_STEP_PX = 130;
/** Under this card height the title is one line instead of two. */
export const TWO_LINE_TITLE_PX = 76;
/** The rows of cards the journey always shows (the owner, 1 Oct 2026: "cut the journey height to 3"). */
export const JOURNEY_ROWS = 3;

export interface JourneySize {
  /** Height of one card, in px. */
  card: number;
  /** Distance from one row's top to the next, in px. */
  step: number;
  /** Height of the poster (2:3), in px. */
  poster: number;
  /** Titles are clamped to one line. */
  oneLine: boolean;
}

/**
 * The mockup's rule, exactly (fitRows' sibling in layout()): the step is the lanes' height, less the 22px kept for "and N
 * more", plus the 8px gap, over three rows, between 68 and 130px; the card is the step less the gap; the poster is 2:3 and
 * 17px shorter than the card (so its width is (card - 17) / 1.5). Without a measured height (a page that scrolls) it is
 * the full 94px step.
 */
export function journeySize(room: number | undefined): JourneySize {
  const step = room === undefined ? ROW_PX : clamp(Math.floor((room - MORE_PX + ROW_GAP_PX) / JOURNEY_ROWS), MIN_TOKEN_PX + ROW_GAP_PX, MAX_STEP_PX);
  const card = step - ROW_GAP_PX;
  return { card, step, poster: card - 17, oneLine: card < TWO_LINE_TITLE_PX };
}

/** The journey keeps its stations side by side when each has room, and stacks them when it does not (a phone, a narrow window). */
export function journeyMode(width: number, stations: number, remPx: number): "columns" | "stacked" {
  if (!(width > 0)) return "columns";
  return width >= stations * MIN_STATION_REM * remPx ? "columns" : "stacked";
}

/** The main area must be at least this wide (in rem) for the right column to sit beside the journey. */
export const SIDE_BY_SIDE_REM = 66;
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
 * bottom row are rows, Sources spans the band and the journey, Turned down today sits beside the bottom row, so the tops and
 * bottoms line up across both columns. Row heights are fluid within bounds, shares of the height the grid has (see `gridRows`).
 */
export const GRID_COLUMNS = "minmax(0, 1fr) clamp(340px, 28%, 600px)";
export const GRID_AREAS = '"now src" "board src" "low act"';
/** System's areas on the same columns and rows: Storage in the top-right slot, Connections beside Health, Backups beside the bottom row. */
export const GRID_AREAS_SYSTEM = '"band store" "mid conn" "low upd"';
/** The bottom row's own two columns, as the mockup's `.low`: Your library and Activity. Turned down today is the right column's last row. */
export const LOW_COLUMNS = "minmax(0, 1.15fr) minmax(0, 1fr)";

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

/** The journey's row at a comfortable size (three rows of 86px cards and the "and N more" line): the mockup's 398px. */
export const JOURNEY_PX = 398;
/**
 * What sits around the journey's lanes: its header (44px), the ribbon (50px) and the space under the lanes (10px) and the card's
 * border (2px), as measured. The mockup's own minimum counts 96, which leaves its lanes 10px short of three rows and their line.
 */
export const JOURNEY_CHROME_PX = 106;
/** The least the journey is given: its chrome, two rows of the smallest cards, one more, and the "and N more" line. */
const JOURNEY_MIN_PX = JOURNEY_CHROME_PX + 2 * (MIN_TOKEN_PX + ROW_GAP_PX) + MIN_TOKEN_PX + MORE_PX;
const BAND_MIN_PX = 150;
const BAND_MAX_PX = 200;
/** Spare height goes to the band, up to this tall. */
const BAND_GROWN_MAX_PX = 240;
const LOW_MIN_PX = 180;
/** The least the bottom row ever gets: once the band and the journey are at their smallest, it gives way down to this rather than the page scrolling or cutting it off (the same rule as Weir's, 2 Oct 2026). */
const LOW_FLOOR_PX = 110;
/** The bottom row is given room up to this before the spare goes to taller journey cards. */
const LOW_ROOMY_PX = 260;
const GRID_GAPS_PX = 24;

/**
 * The rows of the shared grid, the mockup's `fitRows` exactly. `available` is the height the grid has (the mockup's
 * `innerHeight`, its page being the grid); the 20% of it the band takes, the band's limits, the journey's 398px and the
 * bottom row's 180px minimum are the mockup's, and so is the order they give way in: the band first (never under 150px),
 * then the journey (never under what three rows of the smallest cards need). Spare height then goes to the band up to
 * 240px while the bottom row keeps 260px, and the bottom row takes at most a third of the grid (at least 260px): the rest
 * grows the journey's cards up to the 130px step. Without a measured height it is a roomy page (`basis` is ignored when
 * the grid has been measured, so Live and System, which share this grid, always agree).
 */
export function gridRows(basis: number, available = 0): { band: number; journey: number; low: number; template: string } {
  const height = available > 0 ? available : basis > 0 ? basis : 1000;
  const avail = height - GRID_GAPS_PX;
  let band = clamp(Math.round(height * 0.2), BAND_MIN_PX, BAND_MAX_PX);
  let journey = JOURNEY_PX;
  if (band + journey + LOW_MIN_PX > avail) band = Math.max(BAND_MIN_PX, avail - journey - LOW_MIN_PX);
  if (band + journey + LOW_MIN_PX > avail) journey = Math.max(JOURNEY_MIN_PX, avail - band - LOW_MIN_PX);
  let low = avail - band - journey;
  if (low > LOW_ROOMY_PX) {
    const toBand = Math.min(low - LOW_ROOMY_PX, BAND_GROWN_MAX_PX - band);
    band += toBand;
    low -= toBand;
  }
  const lowCap = Math.max(LOW_ROOMY_PX, Math.round(avail * 0.33));
  if (low > lowCap) {
    const grow = Math.min(low - lowCap, JOURNEY_ROWS * (MAX_STEP_PX - ROW_PX));
    journey += grow;
    low -= grow;
  }
  band = Math.round(band);
  journey = Math.round(journey);
  low = Math.max(LOW_FLOOR_PX, Math.round(low));
  return { band, journey, low, template: `${band}px ${journey}px ${low}px` };
}

/** Under the side-by-side threshold the page scrolls, and the journey may be at most most of a window tall. */
export function stackedJourneyBudget(windowHeight: number): number | undefined {
  return windowHeight > 0 ? Math.floor(windowHeight * 0.7) : undefined;
}

/** The height the journey's cards may use: its budget less the header, stations and spacing around them (chrome). */
export function cardsBudget(budget: number | undefined, chrome: number): number | undefined {
  return budget === undefined ? undefined : Math.max(0, budget - chrome);
}

/**
 * Your library (#1103): one shelf row, posters exactly 2:3 and sized from the row's own height, whole posters only. The row
 * takes everything the card has below its chips and counts, so one row gets the whole height.
 */
/** The caption (the title and when) under a poster, and the space between posters, in px. */
export const POSTER_CAPTION_PX = 35;
export const POSTER_GAP_PX = 12;
/** The padding round the posters' box (7 above, 5 below and either side), which the posters do not use. */
const POSTERS_PAD_Y = 12;
const POSTERS_PAD_X = 10;

export interface LibraryPosters {
  /** A caption (title and when) goes under each poster. */
  captions: boolean;
  /** The width of a poster; its height is exactly one and a half times this (2:3). */
  width: number;
}

/** The fewest whole posters the shelf shows across (the owner, 2 Oct 2026, the same rule as Weir's Just finished). */
export const MIN_SHELF_POSTERS = 5;

/**
 * The poster size, from the shelf row's own measured height, and no wider than lets `MIN_SHELF_POSTERS` whole posters fit
 * across the row's measured width. Posters stay exactly 2:3: a narrower poster is also shorter, never stretched.
 */
export function libraryPosters(boxHeight: number, boxWidth = 0): LibraryPosters {
  const rowH = boxHeight - POSTERS_PAD_Y;
  const captions = rowH >= 130;
  const art = Math.max(36, Math.min(255, rowH - (captions ? POSTER_CAPTION_PX : 0)));
  const fromHeight = Math.floor(art / 1.5);
  if (!(boxWidth > 0)) return { captions, width: fromHeight };
  const forFive = Math.floor((boxWidth - POSTERS_PAD_X + POSTER_GAP_PX) / MIN_SHELF_POSTERS - POSTER_GAP_PX);
  return { captions, width: Math.max(24, Math.min(fromHeight, forFive)) };
}

/**
 * A poster narrower than this is too small for its decorations: the upgrade chip and the library's tag would cover a third of
 * the art or more. Weir's Just finished shelf uses the same number and the same rule (2 Oct 2026).
 */
export const FULL_DECORATIONS_MIN_PX = 64;

export type PosterSize = "small" | "full";

/** How a poster of this width is dressed: "full" wears the upgrade chip and the library's whole tag, "small" a slim colour strip. Not measured yet, it is "full". */
export function posterSize(width: number | null | undefined): PosterSize {
  return width != null && width < FULL_DECORATIONS_MIN_PX ? "small" : "full";
}

/** Whole posters across a row of `boxWidth` px, at least one; never a part of one. */
export function postersAcross(boxWidth: number, posterWidth: number): number {
  return Math.max(1, Math.floor((boxWidth - POSTERS_PAD_X + POSTER_GAP_PX) / (posterWidth + POSTER_GAP_PX)));
}
