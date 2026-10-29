import { describe, expect, it } from "vitest";
import { formatSearchOutcome, formatTriggerKind } from "./search-history-format";

describe("the words of a search-history row", () => {
  it.each([
    ["matched", "Matched"], ["no_match", "No match"], ["error", "Error"], ["skipped", "Skipped"], ["pending", "Pending"], ["blocked", "Blocked"]
  ])("the outcome %s reads %s", (outcome, words) => {
    expect(formatSearchOutcome(outcome)).toBe(words);
  });

  it("an outcome it has no word for is capitalised with its dashes and underscores read as spaces", () => {
    expect(formatSearchOutcome("held_for-review")).toBe("Held for review");
  });

  it.each([
    ["manual", "Manual"], ["manual-episode", "Episode"], ["manual-season", "Season"], ["scheduled", "Scheduled"], ["anything-else", "Scheduled"]
  ])("the trigger %s reads %s", (kind, words) => {
    expect(formatTriggerKind(kind)).toBe(words);
  });
});
