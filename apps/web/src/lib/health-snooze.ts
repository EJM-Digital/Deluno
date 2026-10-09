import type { HealthCheckResultItem } from "./api";

/**
 * Whether a health check is snoozed right now. The one rule for every count of problems (#1193): the bell reads
 * the server's summary, which leaves a snoozed problem out, and System's tiles, its Health card and the Dashboard's
 * Health tab say the same by asking here.
 */
export function isHealthCheckSnoozed(check: Pick<HealthCheckResultItem, "snoozedUntilUtc">, now: number = Date.now()): boolean {
  return check.snoozedUntilUtc !== null && check.snoozedUntilUtc !== undefined && Date.parse(check.snoozedUntilUtc) > now;
}

/** The checks that need attention now: not fine, not a suggestion, and not a problem that is snoozed. */
export function healthChecksNeedingAttention<T extends Pick<HealthCheckResultItem, "status" | "snoozedUntilUtc">>(checks: readonly T[], now: number = Date.now()): T[] {
  return checks.filter((check) => check.status !== "ok" && check.status !== "suggestion" && !(check.status === "problem" && isHealthCheckSnoozed(check, now)));
}
