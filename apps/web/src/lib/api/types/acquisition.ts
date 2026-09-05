/**
 * Why a title will not download, and what an override would clear.
 *
 * <p>Mirrors `Deluno.Contracts.AcquisitionBlockers`. The server composes the
 * sentences — every string here is written to be shown, not summarised again
 * on the way past — because the two halves have to agree about what is in the
 * way, and a screen that re-words the reason is a second opinion nobody
 * asked for.</p>
 */

/** The kinds the server can report. Anything else is shown with a neutral tone. */
export const ACQUISITION_BLOCKER_KINDS = {
  alreadyHeld: "already-held",
  downloadInFlight: "download-in-flight",
  processorHoldingFile: "processor-holding-file",
  importExcluded: "import-excluded",
  searchSkipped: "search-skipped",
  searchDeferred: "search-deferred",
  notYetAvailable: "not-yet-available",
  previouslyDownloaded: "previously-downloaded"
} as const;

export interface AcquisitionBlocker {
  kind: string;
  /** Where the record lives — "qBittorrent", "Weir", "Deluno". */
  source: string;
  /** One line, for the row. */
  summary: string;
  /** The longer account, for the line beneath it. */
  detail: string;
  /** Whether a forced re-download would clear this one. */
  canClear: boolean;
  /** What clearing it would do, in the words the person needs before pressing. */
  clearEffect: string | null;
}

export interface AcquisitionBlockersResponse {
  mediaId: string;
  mediaType: string;
  title: string;
  blockers: AcquisitionBlocker[];
  nothingIsBlocking: boolean;
  summary: string;
  canForce: boolean;
}

export interface AcquisitionOverrideResponse {
  mediaId: string;
  cleared: string[];
  couldNotClear: string[];
  searchStarted: boolean;
  summary: string;
}
