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
  previouslyDownloaded: "previously-downloaded",
  releasesBlocked: "releases-blocked"
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

/**
 * A release Deluno has refused, as shown on the blocklist.
 *
 * <p>Mirrors `Deluno.Contracts.BlockedRelease`. The reason is carried as both a
 * code and the import's own sentence, because the screen needs a short label
 * and the person needs the detail.</p>
 */
export interface BlockedRelease {
  id: string;
  releaseKey: string;
  releaseName: string;
  indexerName: string;
  mediaType: string;
  entityId: string | null;
  title: string | null;
  reasonCode: string;
  reason: string;
  torrentHashOrItemId: string | null;
  downloadClientId: string | null;
  downloadClientName: string | null;
  blockedUtc: string;
  /**
   * "refused" or "proposed". A proposal is a question rather than a decision:
   * it changes nothing about what searches do until somebody answers it.
   */
  state: string;
  /**
   * Searches skip this release from every indexer, not only the one it came
   * from. Always true (#585, #874): the blocklist key is the release's
   * identity across every indexer, whatever the refusal reason.
   */
  everyIndexer?: boolean;
  /**
   * Whether a person clicked Refuse (or removed a download from the Queue and
   * said it failed) or Deluno decided this on its own (#800): "manual" or
   * "automatic".
   */
  origin?: string;
}

export interface AcquisitionOverrideResponse {
  mediaId: string;
  cleared: string[];
  couldNotClear: string[];
  searchStarted: boolean;
  summary: string;
}
