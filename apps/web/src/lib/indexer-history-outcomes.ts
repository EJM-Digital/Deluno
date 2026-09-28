/**
 * Plain words for an Indexer History (#754) row's `outcome` (#949).
 *
 * <p>Indexer History renders whichever code the writer used raw, unlike
 * every other outcome/reason chip in Deluno. The codes come from two
 * sources, merged by `IIndexerHistoryService`: the RSS-sync/auth-failure
 * event store (`read`, `failed`) and the existing per-movie/per-series
 * search-history outcome (`matched`, `checked`, `held`, `blocked`) that
 * `AcquisitionDecisionPipeline`/`LibrarySearchJobHandler`/`MediaGrabHandler`
 * write. Anything unrecognised falls back to the code itself, the same as
 * {@link ../lib/failure-reasons.ts}, because a code you can search for beats
 * a word that tells you nothing.</p>
 */

/** Every outcome code Deluno currently writes to an Indexer History row. Kept here so a test can fail the day a new one is added without a word for it. */
export const KNOWN_INDEXER_HISTORY_OUTCOMES = [
  "read",
  "failed",
  "matched",
  "checked",
  "held",
  "blocked"
] as const;

const OUTCOME_WORDS: Record<string, string> = {
  read: "Read the feed",
  failed: "Failed",
  matched: "Matched and sent",
  checked: "Checked, nothing to send",
  held: "Held for review",
  blocked: "Blocked before searching"
};

export function indexerHistoryOutcomeWords(outcome: string): string {
  return OUTCOME_WORDS[outcome] ?? outcome;
}
