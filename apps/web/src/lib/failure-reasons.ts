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
  wrongRelease: "For another show or another episode",
  importFailed: "Failed, with no reason recorded",
  replacementRejected: "Worse than the copy you already had",
  processedFileIsLess: "The processor gave back less than it was given",
  missingLibraryRoot: "The library folder was not there",
  missingSource: "The client said done, and the file was gone",
  permission: "Deluno was not allowed to write there",
  io: "The disk or the network gave way mid-move",
  hardlinkUnavailable: "Hardlinks are not possible here",
  hardlinkFailed: "The hardlink itself failed",
  samePath: "It was already where it belongs",
  conflict: "Something else is already using that name",
  replacementOwnershipMismatch: "That file belongs to a different title",
  processorRejected: "The processor could not use it and removed it",
  downloadStalled: "It stopped downloading and never recovered",
  clientReportedFailure: "Your download client said the download failed",
  deadOrBrokenLink: "The download client could not open the link",
  insufficientFreeSpace: "Not enough free space on the drive",
  downloadIntegrityFailed: "Deluno opened the file and it was wrong",
  unsafeSourcePath: "The file's path climbs out of its folder, so Deluno left it alone"
};

export function reasonWords(reasonCode: string): string {
  return REASON_WORDS[reasonCode] ?? reasonCode;
}

/**
 * Every reason `Deluno.Contracts.ImportFailurePolicy` can store, mirrored here
 * so a test can fail the moment a new one arrives with no words (#967). Keep
 * in step with `ImportFailurePolicy.KnownReasons` by hand — there is no
 * automated cross-language check, same as the rest of this file.
 */
export const KNOWN_IMPORT_FAILURE_REASONS = [
  "noVideoStream",
  "likelySample",
  "unsupportedFile",
  "mediaProbeRejected",
  "mediaProbeUnreadable",
  "unmatched",
  "wrongRelease",
  "importFailed",
  "replacementRejected",
  "missingLibraryRoot",
  "missingSource",
  "permission",
  "io",
  "hardlinkUnavailable",
  "hardlinkFailed",
  "samePath",
  "conflict",
  "replacementOwnershipMismatch",
  "processedFileIsLess",
  "clientReportedFailure",
  "deadOrBrokenLink",
  "insufficientFreeSpace",
  "downloadIntegrityFailed",
  "unsafeSourcePath"
] as const;

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

/** What an import-recovery case's `failureKind` says on a row, in plain words (the Activity list's second line). */
const FAILURE_KIND_WORDS: Record<string, string> = {
  quality: "Quality rejected",
  unmatched: "Needs matching",
  corrupt: "Corrupt",
  downloadFailed: "Download failed",
  importFailed: "Import failed",
  "vanished-downloading": "Download vanished from the client",
  processedFileIsLess: "Processor returned a smaller file",
  "processor-output-missing": "Missing from processor output",
  "processor-unmatched-output": "Unmatched in processor output",
  "processor-lost": "Processor lost the file",
  "processor-timeout": "Processor timed out",
  "processor-stalled": "Processor stalled",
  "processor-failed": "Processor failed",
  "processor-held": "Held by the processor",
  "processor-refuses-every-copy": "Processor refuses every copy"
};

/**
 * A failure kind in words. A kind with no entry is still never printed as a code: camelCase and kebab-case are split
 * into lower-case words and the first is capitalised.
 */
export function failureKindWords(kind: string): string {
  const known = FAILURE_KIND_WORDS[kind] ?? REASON_WORDS[kind];
  if (known) return known;
  const spaced = kind.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[-_]+/g, " ").trim().toLowerCase();
  return spaced ? spaced.charAt(0).toUpperCase() + spaced.slice(1) : "Needs review";
}
