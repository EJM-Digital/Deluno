import { plural } from "../../../lib/plural";
/**
 * Countdown maths for the Live tab (#1096): how long until something runs, and
 * how far through the gap between two runs we are (for a progress ring). Pure
 * functions of a time, so what ticks on screen is only ever the clock.
 */

export interface Countdown {
  /** Milliseconds left; never negative. */
  remainingMs: number;
  /** The run time has arrived. */
  due: boolean;
  /** 0 to 1 through the gap between the last run and the next; null when the last run is not known. */
  fraction: number | null;
  /** "12:04", "1:05:30", "2 days 3 h". Empty when there is no next run. */
  text: string;
}

function parse(value: string | null | undefined): number | null {
  if (!value) return null;
  const time = Date.parse(value);
  return Number.isNaN(time) ? null : time;
}

/** "12:04" under an hour, "1:05:30" under a day, "2 days 3 h" beyond, and "now" when due. */
export function formatCountdown(remainingMs: number): string {
  const seconds = Math.max(0, Math.ceil(remainingMs / 1000));
  if (seconds <= 0) return "now";
  const two = (value: number) => String(value).padStart(2, "0");
  if (seconds < 3600) return `${Math.floor(seconds / 60)}:${two(seconds % 60)}`;
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)}:${two(Math.floor((seconds % 3600) / 60))}:${two(seconds % 60)}`;
  const days = Math.floor(seconds / 86_400);
  const hours = Math.floor((seconds % 86_400) / 3600);
  return `${plural(days, "day")}${hours > 0 ? ` ${hours} h` : ""}`;
}

/** Null when nothing is scheduled. */
export function countdown(nextUtc: string | null | undefined, lastUtc: string | null | undefined, now: number): Countdown | null {
  const next = parse(nextUtc);
  if (next === null) return null;
  const last = parse(lastUtc);
  const remainingMs = Math.max(0, next - now);
  let fraction: number | null = null;
  if (last !== null && next > last) fraction = Math.min(1, Math.max(0, (now - last) / (next - last)));
  return { remainingMs, due: next <= now, fraction, text: formatCountdown(remainingMs) };
}

/** "updated 4 s ago", "updated 2 min ago"; never negative. */
export function updatedAgo(updatedAt: number, now: number): string {
  const seconds = Math.max(0, Math.floor((now - updatedAt) / 1000));
  if (seconds < 5) return "updated just now";
  if (seconds < 90) return `updated ${seconds} s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return `updated ${minutes} min ago`;
  return `updated ${Math.round(minutes / 60)} h ago`;
}
