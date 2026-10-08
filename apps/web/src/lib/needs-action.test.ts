import { describe, expect, it } from "vitest";
import type { DownloadDispatchItem, DownloadTelemetryOverview, MovieImportRecoverySummary, ProcessorHandoffItem, SeriesImportRecoverySummary } from "./api";
import { canTryAgain, isVanished, needsAction, plainFailure } from "./needs-action";

function dispatch(overrides: Partial<DownloadDispatchItem> & Pick<DownloadDispatchItem, "id">): DownloadDispatchItem {
  return {
    libraryId: "library-1",
    mediaType: "movies",
    entityType: "movie",
    entityId: "movie-1",
    releaseName: "Tears.of.Steel.2012.1080p.WEB-DL-DELUNO",
    indexerName: "Fixture",
    downloadClientId: "client-1",
    downloadClientName: "SABnzbd",
    status: "failed",
    notesJson: null,
    createdUtc: "2026-09-20T01:00:00Z",
    grabStatus: "succeeded",
    grabFailureCode: "not_found",
    ...overrides
  } as DownloadDispatchItem;
}

function recovery(cases: Array<{ id: string; dispatchId: string | null }>): MovieImportRecoverySummary {
  return {
    openCount: cases.length,
    qualityCount: 0,
    unmatchedCount: 0,
    corruptCount: 0,
    downloadFailedCount: cases.length,
    importFailedCount: 0,
    recentCases: cases.map((item) => ({
      id: item.id,
      title: "Tears.of.Steel.2012.1080p.WEB-DL-DELUNO",
      failureKind: "grab-timeout",
      summary: "Download was sent to SABnzbd but never appeared in the client's queue after 2 hours.",
      recommendedAction: "Check if the download client is running.",
      detailsJson: item.dispatchId ? JSON.stringify({ DispatchId: item.dispatchId, DownloadClientName: "SABnzbd" }) : null,
      detectedUtc: "2026-09-20T03:00:00Z"
    }))
  };
}

const noSeriesRecovery = recovery([]) as unknown as SeriesImportRecoverySummary;

describe("needsAction", () => {
  // #557, the rig on 22 September: "Needs action 20" for nine problems.
  it("shows a timed-out download once, as the case that can be retried or dismissed", () => {
    const result = needsAction({
      movieRecovery: recovery([{ id: "case-1", dispatchId: "d1" }]),
      seriesRecovery: noSeriesRecovery,
      dispatches: [dispatch({ id: "d1" })]
    });

    expect(result.recoveryCases.map((item) => item.id)).toEqual(["case-1"]);
    expect(result.failedDownloads).toEqual([]);
    expect(result.total).toBe(1);
  });

  // #1167: the Nosferatu import was listed as its dead-lettered job and again as its recovery case.
  it("lists an import that failed once, not as the failed job and again as its case", () => {
    const job = {
      id: "job-1", jobType: "filesystem.import.execute", status: "dead-letter", attempts: 3, maxAttempts: 3, nextAttemptUtc: null,
      payloadJson: JSON.stringify({ DispatchId: "d1" }), lastError: "The destination file already exists."
    } as never;
    const otherJob = { ...(job as object), id: "job-2", payloadJson: JSON.stringify({ DispatchId: "d-other" }) } as never;

    const result = needsAction({
      movieRecovery: recovery([{ id: "case-1", dispatchId: "d1" }]),
      seriesRecovery: noSeriesRecovery,
      dispatches: [dispatch({ id: "d1" })],
      failedJobs: [job, otherJob]
    });

    expect(result.recoveryCases.map((item) => item.id)).toEqual(["case-1"]);
    expect(result.failedImportJobs.map((item) => item.id)).toEqual(["job-2"]);
  });

  it("folds a release refused on every cycle into one row that says how often", () => {
    const blocked = (id: string, createdUtc: string) =>
      dispatch({ id, status: "blocked", grabStatus: "blocked", grabFailureCode: null, releaseName: "Big.Buck.Bunny.2008.1080p.WEB-DL-DELUNO", entityId: "movie-bbb", createdUtc });

    const result = needsAction({
      dispatches: [blocked("b1", "2026-09-20T01:27:00Z"), blocked("b2", "2026-09-21T20:17:00Z"), blocked("b3", "2026-09-20T20:16:00Z")]
    });

    expect(result.failedDownloads).toHaveLength(1);
    expect(result.failedDownloads[0].attempts).toBe(3);
    expect(result.failedDownloads[0].dispatch.id).toBe("b2");
    expect(result.total).toBe(1);
  });

  it("folds a release chosen on Search by its name, whatever generated id each grab got (#1062)", () => {
    const chosen = (id: string, entityId: string) => dispatch({ id, entityType: "release", entityId });

    const result = needsAction({ dispatches: [chosen("d1", "aaa"), chosen("d2", "bbb")] });

    expect(result.failedDownloads).toHaveLength(1);
    expect(result.failedDownloads[0].attempts).toBe(2);
  });

  it("keeps different releases, and different titles, apart", () => {
    const result = needsAction({
      dispatches: [
        dispatch({ id: "d1" }),
        dispatch({ id: "d2", releaseName: "Tears.of.Steel.2012.2160p.WEB-DL-DELUNO" }),
        dispatch({ id: "d3", entityId: "movie-2" })
      ]
    });

    expect(result.failedDownloads).toHaveLength(3);
  });

  it("shows a failed download once when its import issue names it inside the retry (#602)", () => {
    const series = {
      ...recovery([]),
      recentCases: [{ ...recovery([{ id: "case-1", dispatchId: null }]).recentCases[0], detailsJson: JSON.stringify({ RetryRequest: { DispatchId: "d1" } }) }]
    };

    const result = needsAction({ seriesRecovery: series, dispatches: [dispatch({ id: "d1" })] });

    expect(result.recoveryCases).toHaveLength(1);
    expect(result.failedDownloads).toHaveLength(0);
    expect(result.total).toBe(1);
  });

  it("shows the client's copy of a download its case is about only as that case (#603)", () => {
    const refused = { id: "hash-1", clientId: "qbit", releaseName: "The.Cabinet.of.Dr.Caligari.1920.1080p.WEB-DL-CLIPPED", status: "importFailed", errorMessage: "The download client reported queue status 'importFailed'." };
    const other = { ...refused, id: "hash-2", releaseName: "Something.Else.2020.1080p" };
    const telemetry = { clients: [{ queue: [refused, other] }] } as unknown as DownloadTelemetryOverview;

    const result = needsAction({
      movieRecovery: recovery([{ id: "case-1", dispatchId: "d1" }]),
      dispatches: [dispatch({ id: "d1", downloadClientId: "qbit", torrentHashOrItemId: "HASH-1" })],
      telemetry
    });

    expect(result.queueAttention.map((item) => item.id)).toEqual(["hash-2"]);
    expect(result.total).toBe(2);
  });

  // #914, #929: the handoff row carries Import original, Retry, Stop waiting
  // and Dismiss; the case naming the same hand-off cannot do any of that, and
  // showed the same problem twice on the rig.
  it("shows a stalled or lost hand-off once, as the hand-off row that can act on it", () => {
    const handoff: ProcessorHandoffItem = {
      id: "handoff-1",
      libraryId: "library-1",
      mediaType: "movie",
      clientId: "client-1",
      queueItemId: "queue-1",
      releaseName: "Sintel.2010.1080p",
      sourcePath: "/downloads/sintel",
      processorName: "Weir",
      status: "stalled",
      outputPath: null,
      importJobId: null,
      failureMessage: "Weir reported no change for 2 days.",
      createdUtc: "2026-09-18T12:00:00Z",
      updatedUtc: "2026-09-20T09:00:00Z"
    };
    const caseAboutTheHandoff = {
      id: "case-1",
      title: "Sintel.2010.1080p",
      failureKind: "processor-stalled",
      summary: "Weir reported no change for 2 days.",
      recommendedAction: "Import the original now, keep waiting, ask the processor again, or give up and search for another release.",
      detailsJson: JSON.stringify({ HandoffId: "handoff-1", LibraryId: "library-1" }),
      detectedUtc: "2026-09-20T09:00:00Z"
    };

    const result = needsAction({
      movieRecovery: { openCount: 1, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [caseAboutTheHandoff] },
      seriesRecovery: noSeriesRecovery,
      handoffs: [handoff]
    });

    expect(result.recoveryCases).toEqual([]);
    expect(result.failedHandoffs.map((item) => item.id)).toEqual(["handoff-1"]);
    expect(result.total).toBe(1);
  });

  it("lists a failed hand-off's download once, as the hand-off row, not also as the client's item", () => {
    const handoff = {
      id: "h1", libraryId: "library-1", mediaType: "movies", clientId: "client-1", queueItemId: "hash-1", releaseName: "Oppenheimer.2023.REPACK",
      sourcePath: "/downloads/oppenheimer", processorName: "Weir", status: "failed", outputPath: null, importJobId: null,
      failureMessage: "The processor refused the hand-off with HTTP 400", createdUtc: "2026-10-07T03:00:00Z", updatedUtc: "2026-10-07T03:00:00Z"
    } as ProcessorHandoffItem;
    const item = { id: "hash-1", clientId: "client-1", status: "processingFailed", errorMessage: handoff.failureMessage };
    const telemetry = { clients: [{ queue: [item] }] } as unknown as DownloadTelemetryOverview;

    const result = needsAction({ telemetry, handoffs: [handoff] });

    expect(result.failedHandoffs).toHaveLength(1);
    expect(result.queueAttention).toHaveLength(0);
    expect(result.total).toBe(1);
  });

  it("lists Oppenheimer once when the list carries only the case's key fields (#1167)", () => {
    // What the import-recovery list returns without the full details: the keys that say which hand-off and
    // download the case is about, and nothing else.
    const handoff = {
      id: "01a11a4ad91f7d38b80db6de7a9c399c", libraryId: "library-1", mediaType: "movies", clientId: "client-1", queueItemId: "hash-1", releaseName: "Oppenheimer.2023.REPACK",
      sourcePath: "/downloads/oppenheimer", processorName: "Weir", status: "failed", outputPath: null, importJobId: null,
      failureMessage: "The processor refused the hand-off", createdUtc: "2026-10-07T03:00:00Z", updatedUtc: "2026-10-07T03:00:00Z"
    } as ProcessorHandoffItem;
    const keyOnlyCase = {
      id: "case-1",
      title: "Oppenheimer.2023.REPACK",
      failureKind: "processor-failed",
      summary: "Weir reports failed",
      recommendedAction: "Review",
      detailsJson: JSON.stringify({ dispatchId: null, handoffId: handoff.id, clientId: "client-1", queueItemId: "hash-1" }),
      detectedUtc: "2026-10-07T03:00:00Z"
    };

    const result = needsAction({
      movieRecovery: { openCount: 1, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [keyOnlyCase] },
      seriesRecovery: noSeriesRecovery,
      handoffs: [handoff]
    });

    expect(result.recoveryCases).toEqual([]);
    expect(result.failedHandoffs).toHaveLength(1);
    expect(result.total).toBe(1);
  });

  it("counts each orphaned download folder once, beside everything else (#588)", () => {
    const orphan = {
      path: "C:\\Deluno\\Downloads-Complete\\TV\\Breaking.Bad.S01E01.1080p.WEB-DL.x264-DELUNO.1",
      name: "Breaking.Bad.S01E01.1080p.WEB-DL.x264-DELUNO.1",
      libraryId: "library-tv",
      libraryName: "TV",
      sizeBytes: 2_800_000_000,
      lastWrittenUtc: "2026-09-15T23:10:00Z"
    };

    const result = needsAction({ orphans: [orphan] });

    expect(result.orphanedDownloads).toEqual([orphan]);
    expect(result.total).toBe(1);
  });

  it("counts one per row, and nothing when nothing needs a person", () => {
    expect(needsAction({}).total).toBe(0);
    expect(needsAction({ dispatches: [dispatch({ id: "fine", status: "sent", grabFailureCode: null })] }).total).toBe(0);

    const result = needsAction({
      movieRecovery: recovery([{ id: "case-1", dispatchId: "d1" }, { id: "case-2", dispatchId: null }]),
      dispatches: [dispatch({ id: "d1" }), dispatch({ id: "d9", entityId: "movie-9" })]
    });
    const rows = result.recoveryCases.length + result.queueAttention.length + result.failedImportJobs.length
      + result.failedHandoffs.length + result.failedDownloads.length;

    expect(result.total).toBe(rows);
    expect(result.total).toBe(3);
  });
});

describe("canTryAgain", () => {
  const named = JSON.stringify({ DispatchId: "dispatch-1" });

  it("offers Try again for a failed download or import that names its download", () => {
    expect(canTryAgain({ failureKind: "downloadFailed", detailsJson: named })).toBe(true);
    expect(canTryAgain({ failureKind: "importFailed", detailsJson: named })).toBe(true);
  });

  // #927: search for another is offered the same way a failed download is.
  it("offers it for a vanished download too", () => {
    expect(canTryAgain({ failureKind: "vanished-downloading", detailsJson: named })).toBe(true);
  });

  // Seen on the rig: a processor failure names a hand-off, and Try again could only say "dismiss it".
  it("does not offer it for a case that names no download", () => {
    expect(canTryAgain({ failureKind: "importFailed", detailsJson: JSON.stringify({ HandoffId: "handoff-1" }) })).toBe(false);
    expect(canTryAgain({ failureKind: "downloadFailed", detailsJson: null })).toBe(false);
  });

  it("does not offer it where a decision is needed", () => {
    expect(canTryAgain({ failureKind: "quality", detailsJson: named })).toBe(false);
  });
});

describe("isVanished", () => {
  it("is true only for a vanished-downloading case", () => {
    expect(isVanished({ failureKind: "vanished-downloading" })).toBe(true);
    expect(isVanished({ failureKind: "downloadFailed" })).toBe(false);
  });
});

// #1167, the rc.30 live test: 88 rows overnight for downloads Deluno had already dealt with.
describe("needsAction: what Deluno handled itself is not for the user", () => {
  it("does not list a download the stall rules blocked, removed and replaced", () => {
    const handled = dispatch({
      id: "stalled",
      status: "sent",
      grabFailureCode: null,
      importStatus: "failed",
      importFailureCode: "health-remediation-applied",
      importFailureMessage: "release refused; replacement search queued; client entry removed (payload retained by client)"
    });

    const result = needsAction({ dispatches: [handled] });

    expect(result.failedDownloads).toEqual([]);
    expect(result.total).toBe(0);
  });

  it("still lists a download whose import was tried and failed", () => {
    const failed = dispatch({ id: "bad", status: "sent", grabFailureCode: null, importStatus: "failed", importFailureCode: "no-video-file", importFailureMessage: "No video file." });

    expect(needsAction({ dispatches: [failed] }).total).toBe(1);
  });

  it("drops a failed hand-off once its download was imported", () => {
    const imported = dispatch({ id: "done", status: "imported", grabFailureCode: null, importStatus: "imported", torrentHashOrItemId: "hash-1" });
    const handoff = {
      id: "h1", libraryId: "library-1", mediaType: "movies", clientId: "client-1", queueItemId: "hash-1", releaseName: "Oppenheimer.2023",
      sourcePath: "/downloads/oppenheimer", processorName: "Weir", status: "failed", outputPath: null, importJobId: null,
      failureMessage: "The processor refused the hand-off with HTTP 400", createdUtc: "2026-10-07T03:00:00Z", updatedUtc: "2026-10-07T03:00:00Z"
    } as ProcessorHandoffItem;

    expect(needsAction({ handoffs: [handoff], dispatches: [] }).failedHandoffs).toHaveLength(1);
    expect(needsAction({ handoffs: [handoff], dispatches: [imported] }).failedHandoffs).toHaveLength(0);
  });

  it("drops a recovery case once its download was imported", () => {
    const imported = dispatch({ id: "d1", status: "imported", grabFailureCode: null, importStatus: "imported" });

    const result = needsAction({
      movieRecovery: recovery([{ id: "case-1", dispatchId: "d1" }]),
      seriesRecovery: noSeriesRecovery,
      dispatches: [imported]
    });

    expect(result.recoveryCases).toEqual([]);
    expect(result.total).toBe(0);
  });
});

describe("needsAction: one failure is one row (#1167)", () => {
  it("shows a dead-lettered import, its recovery case and its hand-off as the one hand-off row", () => {
    const job = {
      id: "job-1", jobType: "filesystem.import.execute", status: "dead-letter", attempts: 3, maxAttempts: 3, nextAttemptUtc: null,
      payloadJson: JSON.stringify({ DispatchId: "d1" }), lastError: "The import failed."
    } as never;
    const handoff = {
      id: "h1", clientId: "client-1", queueItemId: "hash-1", releaseName: "Nosferatu.1922", status: "failed",
      failureMessage: "The processor lost the audio.", importJobId: "job-1"
    } as unknown as ProcessorHandoffItem;

    const result = needsAction({
      movieRecovery: recovery([{ id: "case-1", dispatchId: "d1" }]),
      seriesRecovery: noSeriesRecovery,
      dispatches: [dispatch({ id: "d1", torrentHashOrItemId: "hash-1" })],
      failedJobs: [job],
      handoffs: [handoff]
    });

    expect(result.failedHandoffs.map((item) => item.id)).toEqual(["h1"]);
    expect(result.recoveryCases).toEqual([]);
    expect(result.failedImportJobs).toEqual([]);
    expect(result.failedDownloads).toEqual([]);
    expect(result.total).toBe(1);
  });

  it("still lists a different film's failure beside it", () => {
    const handoff = { id: "h1", clientId: "client-1", queueItemId: "hash-1", releaseName: "A", status: "failed", failureMessage: "x", importJobId: null } as unknown as ProcessorHandoffItem;
    const result = needsAction({
      seriesRecovery: noSeriesRecovery,
      dispatches: [dispatch({ id: "d1", torrentHashOrItemId: "hash-1" }), dispatch({ id: "d2", entityId: "movie-2", torrentHashOrItemId: "hash-2" })],
      handoffs: [handoff]
    });
    expect(result.total).toBe(2);
  });
});

// #1167: Oppenheimer REPACK was a recovery case and a failed hand-off; Nosferatu a recovery case and a dispatch failure.
describe("needsAction merges the records of one download by the download's own key", () => {
  const handoff = { id: "h-new", clientId: "client-1", queueItemId: "hash-1", releaseName: "Oppenheimer", status: "failed", failureMessage: "x", importJobId: null } as unknown as ProcessorHandoffItem;
  const caseFor = (details: object | null) => ({
    ...recovery([]),
    openCount: 1,
    recentCases: [{
      id: "case-1", title: "Oppenheimer", failureKind: "processor-held", summary: "s", recommendedAction: "r",
      detailsJson: details ? JSON.stringify(details) : null, detectedUtc: "2026-09-20T03:00:00Z"
    }]
  }) as unknown as MovieImportRecoverySummary;

  it("shows a case that names the download's client entry once, as the failed hand-off, whichever hand-off id it carries", () => {
    const result = needsAction({
      movieRecovery: caseFor({ handoffId: "h-older-dismissed-away", clientId: "client-1", queueItemId: "hash-1" }),
      seriesRecovery: noSeriesRecovery,
      handoffs: [handoff]
    });
    expect(result.failedHandoffs.map((item) => item.id)).toEqual(["h-new"]);
    expect(result.recoveryCases).toEqual([]);
    expect(result.total).toBe(1);
  });

  it("shows a dispatch failure once, as the case that names the same client entry", () => {
    const result = needsAction({
      movieRecovery: caseFor({ clientId: "client-1", queueItemId: "hash-1" }),
      seriesRecovery: noSeriesRecovery,
      dispatches: [dispatch({ id: "d1", torrentHashOrItemId: "hash-1" })]
    });
    expect(result.recoveryCases.map((item) => item.id)).toEqual(["case-1"]);
    expect(result.failedDownloads).toEqual([]);
    expect(result.total).toBe(1);
  });

  it("keeps a case that names a different download", () => {
    const result = needsAction({
      movieRecovery: caseFor({ clientId: "client-1", queueItemId: "hash-9" }),
      seriesRecovery: noSeriesRecovery,
      handoffs: [handoff]
    });
    expect(result.total).toBe(2);
  });
});

describe("plainFailure", () => {
  it("never leaves a bare 'Error: timed out'", () => {
    expect(plainFailure("Error: timed out", "Sending it to Weir")).toBe("Sending it to Weir timed out. If the title is already where it belongs, dismiss this; otherwise try again.");
  });

  it("leaves a message that already says something as it is", () => {
    expect(plainFailure("Weir's watched folder is not set for this media scope.", "Sending it to Weir")).toBe("Weir's watched folder is not set for this media scope.");
  });
});
