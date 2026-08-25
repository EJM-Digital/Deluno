/**
 * How far back the dashboard's trend charts read.
 *
 * `/api/dashboard/metrics` accepts any window it can clamp to 1..3650, so these
 * are the useful windows rather than the possible ones: a week to see the last
 * few nights, a month as the default shape, a quarter for a seasonal read, a
 * year for "has this install ever been busy".
 *
 * It lives here rather than in the route so the choice, its storage and the way
 * a window is written into a subtitle can be tested without standing up a
 * router, a query client and twenty loader fixtures.
 */
export const HISTORY_RANGES = [
  { value: "7", label: "7d" },
  { value: "30", label: "30d" },
  { value: "90", label: "90d" },
  { value: "365", label: "1y" }
] as const;

export type HistoryDays = (typeof HISTORY_RANGES)[number]["value"];

/** Matches the window the route loader fetches; changing one means changing both. */
export const DEFAULT_HISTORY_DAYS: HistoryDays = "30";

export const HISTORY_STORAGE_KEY = "deluno.dashboard.history-days";

export function isHistoryDays(value: unknown): value is HistoryDays {
  return HISTORY_RANGES.some((range) => range.value === value);
}

/**
 * Anything that is not a window we offer falls back to the default — including
 * a stale value left by an older build, which is why this validates rather than
 * casting whatever was stored.
 */
export function readStoredHistoryDays(): HistoryDays {
  if (typeof window === "undefined") return DEFAULT_HISTORY_DAYS;
  try {
    const raw = window.localStorage.getItem(HISTORY_STORAGE_KEY);
    if (isHistoryDays(raw)) return raw;
  } catch {
    /* noop: private mode and blocked storage both just mean "use the default" */
  }
  return DEFAULT_HISTORY_DAYS;
}

export function writeStoredHistoryDays(days: HistoryDays) {
  try {
    window.localStorage.setItem(HISTORY_STORAGE_KEY, days);
  } catch {
    /* noop: the choice still holds for this session */
  }
}

/**
 * How a window is written into a subtitle. Every chart the range governs says
 * it, so changing the control visibly changes all three rather than one. The
 * count comes from the response rather than the request, so a window the server
 * clamped is reported as what was actually read.
 */
export function windowLabel(days: number) {
  if (days === 1) return "the last day";
  if (days === 7) return "the last week";
  if (days === 365) return "the last year";
  if (days % 365 === 0) return `the last ${days / 365} years`;
  if (days % 7 === 0 && days < 30) return `the last ${days / 7} weeks`;
  return `the last ${days} days`;
}
