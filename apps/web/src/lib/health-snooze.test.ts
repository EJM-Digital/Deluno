import { describe, expect, it } from "vitest";
import { healthChecksNeedingAttention, isHealthCheckSnoozed } from "./health-snooze";

const now = Date.parse("2026-10-09T10:00:00Z");
const check = (status: string, snoozedUntilUtc: string | null) => ({ status, snoozedUntilUtc }) as never;

describe("the one snooze rule for counts of problems (#1193)", () => {
  it("is snoozed only until the snooze ends", () => {
    expect(isHealthCheckSnoozed(check("problem", "2026-10-10T10:00:00Z"), now)).toBe(true);
    expect(isHealthCheckSnoozed(check("problem", "2026-10-09T09:59:00Z"), now)).toBe(false);
    expect(isHealthCheckSnoozed(check("problem", null), now)).toBe(false);
  });

  it("counts a snoozed problem nowhere until the snooze ends, and leaves suggestions and passing checks out", () => {
    const checks = [check("problem", "2026-10-10T10:00:00Z"), check("problem", null), check("unknown", null), check("ok", null), check("suggestion", null)];
    expect(healthChecksNeedingAttention(checks, now)).toHaveLength(2);
    expect(healthChecksNeedingAttention(checks, Date.parse("2026-10-11T00:00:00Z"))).toHaveLength(3);
  });
});
