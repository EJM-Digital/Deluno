import { describe, expect, it } from "vitest";
import { indexerHistoryOutcomeWords, KNOWN_INDEXER_HISTORY_OUTCOMES } from "./indexer-history-outcomes";

/**
 * #949: Indexer History used to render `item.outcome` raw. Every outcome
 * code Deluno currently writes must have a plain-words entry -- this fails
 * the moment one is added to KNOWN_INDEXER_HISTORY_OUTCOMES without a word
 * for it, the same way a code missing from failure-reasons.ts would fall
 * back to itself unnoticed.
 */
describe("indexerHistoryOutcomeWords", () => {
  it.each(KNOWN_INDEXER_HISTORY_OUTCOMES)("gives plain words for %s, not the raw code", (outcome) => {
    const words = indexerHistoryOutcomeWords(outcome);
    expect(words).not.toEqual(outcome);
    expect(words.length).toBeGreaterThan(0);
  });

  it("falls back to the code itself for an outcome nobody has named yet", () => {
    expect(indexerHistoryOutcomeWords("some-new-outcome")).toBe("some-new-outcome");
  });
});
