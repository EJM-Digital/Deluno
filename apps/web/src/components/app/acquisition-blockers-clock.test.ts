import { describe, expect, it } from "vitest";
import { withClockTime } from "./acquisition-blockers-card";

describe("withClockTime (#1138)", () => {
  it("adds the clock time to a wait, never a raw UTC stamp", () => {
    const shown = withClockTime("Deluno will search again in about 6 hours.", "2026-10-08T12:03:00Z");
    expect(shown).toMatch(/^Deluno will search again in about 6 hours \(\d{1,2}:03 [ap]m\)\.$/);
    expect(shown).not.toMatch(/\d{4}-\d{2}-\d{2}/);
  });

  it("leaves a reason that is not a wait as it is", () => {
    expect(withClockTime("Nothing to wait for.", null)).toBe("Nothing to wait for.");
    expect(withClockTime("Nothing to wait for.", "not a date")).toBe("Nothing to wait for.");
  });

  it("changes only the full stop that ends the sentence, and ends it again after the clock", () => {
    expect(withClockTime("Wait. Then search again in 6 hours.", "2026-10-08T12:03:00Z")).toMatch(/^Wait\. Then search again in 6 hours \(\d{1,2}:03 [ap]m\)\.$/);
    expect(withClockTime("Try later", "2026-10-08T12:03:00Z")).toMatch(/^Try later \(\d{1,2}:03 [ap]m\)\.$/);
  });

  it("shows the hour without a leading zero and the minutes with two digits, in lower case", () => {
    expect(withClockTime("x.", "2026-10-08T09:05:00")).toMatch(/^x \([1-9]\d?:05 [ap]m\)\.$/);
  });
});
