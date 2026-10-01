import type { QueryClient } from "@tanstack/react-query";
import type { DashboardSummary, DashboardSummaryUpdatedEvent } from "./api/types";

/** The query key the Dashboard summary lives under (the same one routes/dashboard/sources.ts reads). */
export const DASHBOARD_SUMMARY_KEY = ["dashboard", "summary"] as const;

function stamp(value: unknown): number {
  const at = (value as { asOfUtc?: string } | null | undefined)?.asOfUtc;
  const parsed = at ? Date.parse(at) : Number.NaN;
  return Number.isNaN(parsed) ? Number.NEGATIVE_INFINITY : parsed;
}

/**
 * Writes the sections the server pushed straight into the cached Dashboard
 * summary (#1097), with no fetch afterwards. A section only replaces what is
 * cached when it is at least as new, so a replayed event after a reconnect can
 * never put older numbers over a newer fetch. With nothing cached yet there is
 * nothing to merge into, and the screen first fetch fills it.
 *
 * Returns whether anything was applied.
 */
export function applyDashboardSummaryUpdate(queryClient: QueryClient, update: DashboardSummaryUpdatedEvent): boolean {
  const current = queryClient.getQueryData<DashboardSummary>(DASHBOARD_SUMMARY_KEY);
  if (!current || !update?.sections) return false;

  const next: DashboardSummary = { ...current };
  const applied = new Set<string>();
  for (const [name, section] of Object.entries(update.sections)) {
    if (section == null) continue;
    const existing = (current as unknown as Record<string, unknown>)[name];
    if (existing != null && stamp(section) < stamp(existing)) continue;
    (next as unknown as Record<string, unknown>)[name] = section;
    applied.add(name);
  }
  if (applied.size === 0) return false;

  const failed = (update.unavailable ?? []).filter((name) => !applied.has(name));
  next.unavailableSections = [
    ...current.unavailableSections.filter((name) => !applied.has(name)),
    ...failed.filter((name) => !current.unavailableSections.includes(name))
  ];
  if (Date.parse(update.asOfUtc) >= Date.parse(current.asOfUtc)) {
    next.asOfUtc = update.asOfUtc;
  }

  queryClient.setQueryData(DASHBOARD_SUMMARY_KEY, next);
  return true;
}
