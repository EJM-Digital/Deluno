/**
 * Facts that stand beside what Deluno is doing (#1096).
 *
 * - The quality share is how much of the library meets its goal; System shows it.
 */
import type { DashboardSummary } from "../../../lib/api/types/operations";

/** The share of titles meeting their goal, as a whole percent; null when there is nothing in the library. */
export function qualityShare(summary: DashboardSummary | undefined): { percent: number; met: number; total: number; upgradable: number } | null {
  const library = summary?.library;
  if (!library) return null;
  const total = library.movies.total + library.shows.total;
  if (total <= 0) return null;
  const met = Math.min(total, library.movies.qualityMet + library.shows.qualityMet);
  return { percent: Math.round((met / total) * 100), met, total, upgradable: library.movies.upgradable + library.shows.upgradable };
}
