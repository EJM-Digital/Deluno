/**
 * The seventeen ways an import can fail, said the way a person would say them.
 *
 * <p>Shared by the blocklist and the rules screen, which is the point. Both
 * name the same failures, and two copies of these words would have drifted the
 * first time one of them was reworded.</p>
 *
 * <p>Mirrors `Deluno.Contracts.ImportFailurePolicy` and `FailureCategories`.
 * Anything unrecognised falls back to the code itself rather than to "unknown",
 * because a code you can search for beats a word that tells you nothing.</p>
 */

export type BlockDecision = "Never" | "AfterOneRetry" | "Immediately" | "AskMe";

/** Mirrors `Deluno.Contracts.ImportFailureRule`. */
export interface ImportFailureRule {
  reasonCode: string;
  category: string;
  decision: BlockDecision;
  defaultDecision: BlockDecision;
  isOverridden: boolean;
}

const REASON_WORDS: Record<string, string> = {
  noVideoStream: "No video in the file",
  likelySample: "A sample, not the film",
  unsupportedFile: "Not a media file Deluno accepts",
  mediaProbeRejected: "Unreadable — corrupt or encrypted",
  mediaProbeUnreadable: "Could not be read at the time",
  unmatched: "Deluno could not tell which title it is",
  importFailed: "Failed, with no reason recorded",
  replacementRejected: "Worse than the copy you already had",
  missingLibraryRoot: "The library folder was not there",
  missingSource: "The client said done, and the file was gone",
  permission: "Deluno was not allowed to write there",
  io: "The disk or the network gave way mid-move",
  hardlinkUnavailable: "Hardlinks are not possible here",
  hardlinkFailed: "The hardlink itself failed",
  samePath: "It was already where it belongs",
  conflict: "Something else is already using that name",
  replacementOwnershipMismatch: "That file belongs to a different title"
};

export function reasonWords(reasonCode: string): string {
  return REASON_WORDS[reasonCode] ?? reasonCode;
}

/**
 * The headings, and why each group is answered the way it is.
 *
 * <p>Printed alphabetically, seventeen codes read as an inventory and give the
 * reader nothing to decide with. Grouped by whose fault it was, they read as
 * the argument — and the argument is what tells somebody whether "refuse
 * immediately" is reasonable for that row.</p>
 */
export const FAILURE_CATEGORIES: { id: string; title: string; blurb: string }[] = [
  {
    id: "badFile",
    title: "The file was wrong",
    blurb: "Deluno read it and it was not what was wanted. Another copy of the same release is the same file."
  },
  {
    id: "cannotSay",
    title: "Deluno could not say",
    blurb: "It failed, and nothing in the failure says whose fault that was."
  },
  {
    id: "yourSetup",
    title: "Your setup, not the release",
    blurb: "These fail the same way for every title. Refusing here is how a blocklist fills with releases that were never at fault."
  },
  {
    id: "notAFailure",
    title: "Not a failure at all",
    blurb: "Deluno comparing two copies and keeping the better one."
  }
];

/** The four answers, in the order of how much they do. */
export const DECISION_OPTIONS: { value: BlockDecision; label: string }[] = [
  { value: "Never", label: "Never" },
  { value: "AfterOneRetry", label: "One retry" },
  { value: "Immediately", label: "At once" },
  { value: "AskMe", label: "Ask me" }
];

export function decisionWords(decision: BlockDecision): string {
  switch (decision) {
    case "Never":
      return "keeps offering it";
    case "AfterOneRetry":
      return "tries once more, then refuses it";
    case "Immediately":
      return "refuses it straight away";
    case "AskMe":
      return "asks you first";
  }
}
