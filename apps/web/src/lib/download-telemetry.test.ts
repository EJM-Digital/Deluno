import { describe, expect, it } from "vitest";
import { CLIENT_HISTORY_OUTCOMES, clientHistoryOutcomeLabel } from "./download-telemetry";

describe("clientHistoryOutcomeLabel", () => {
  it.each(CLIENT_HISTORY_OUTCOMES)(
    "has plain words for the known client-history outcome %s",
    (outcome) => {
      const words = clientHistoryOutcomeLabel(outcome);
      // A missing entry falls back to the raw code, which is the exact leak
      // #948 was filed over — "needsAttention" on screen instead of words.
      expect(words).not.toBe(outcome);
      expect(words.length).toBeGreaterThan(0);
    }
  );

  it("falls back to the raw code for an outcome nobody has answered yet", () => {
    expect(clientHistoryOutcomeLabel("somethingBrandNew")).toBe("somethingBrandNew");
  });
});
