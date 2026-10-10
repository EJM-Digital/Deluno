/**
 * How a download's percentage is written everywhere it is shown (#1236): Transfers, the drawer, the Dashboard's cards and
 * the update download. It is floored to the shown precision, never rounded, so a download that is not complete never
 * reads 100%. A client that reports 99.9916 says "99.9%", not "100%"; only a value of 100 or more says "100%".
 */

/** Added before flooring so 29 (held as 28.999999999999996) still reads 29. */
const EPSILON = 1e-9;

/**
 * The percentage as text. Whole numbers up to 98, then one decimal (99.9%) so the last stretch of a download is visible.
 * `decimals` fixes the shown precision (2 gives 99.99%). Always floored; 100% only for 100 or more.
 */
export function formatProgressPercent(value: number | null | undefined, decimals?: number): string {
  const shown = floorProgress(value, decimals);
  return `${shown}%`;
}

/** The same number without its sign, floored to the shown precision (what a bar or a sort can use). */
export function floorProgress(value: number | null | undefined, decimals?: number): number {
  if (value === null || value === undefined || !Number.isFinite(value) || value <= 0) return 0;
  if (value >= 100) return 100;
  const places = decimals ?? (value >= 99 ? 1 : 0);
  const scale = 10 ** places;
  const floored = Math.floor(value * scale + EPSILON) / scale;
  // Flooring can never reach 100 from below 100, but an epsilon at 99.9999999995 must not either.
  return Math.min(floored, 100 - 1 / scale);
}
