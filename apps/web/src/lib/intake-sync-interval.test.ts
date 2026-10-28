import { describe, expect, it } from "vitest";
import { INTAKE_SYNC_PRESETS, MAX_INTAKE_SYNC_INTERVAL_HOURS } from "./intake-sync-interval";

describe("import list check presets", () => {
  it("offers only intervals the server accepts", () => {
    expect(MAX_INTAKE_SYNC_INTERVAL_HOURS).toBe(168);
    for (const preset of INTAKE_SYNC_PRESETS) {
      const hours = Number(preset.value);
      expect(hours).toBeGreaterThan(0);
      expect(hours).toBeLessThanOrEqual(MAX_INTAKE_SYNC_INTERVAL_HOURS);
    }
  });

  it("offers each preset once, in order, ending at weekly", () => {
    const hours = INTAKE_SYNC_PRESETS.map((preset) => Number(preset.value));
    expect(hours).toEqual([6, 12, 24, 72, 168]);
    expect(INTAKE_SYNC_PRESETS.map((preset) => preset.label)).toEqual(["Every 6 hours", "Every 12 hours", "Daily", "Every 3 days", "Weekly"]);
    expect(INTAKE_SYNC_PRESETS.some((preset) => preset.label === "Fortnightly" || preset.label === "Monthly")).toBe(false);
  });
});
