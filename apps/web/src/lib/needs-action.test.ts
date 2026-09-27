import { describe, expect, it } from "vitest";
import type { DownloadDispatchItem, DownloadTelemetryOverview, MovieImportRecoverySummary, ProcessorHandoffItem, SeriesImportRecoverySummary } from "./api";
import { canTryAgain, isVanished, needsAction } from "./needs-action";

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

  it("counts each orphaned download folder once, beside everything else (#588)", () => {
    const orphan = {
      path: "C:\\Deluno\\Downloads-Complete\\TV\\Breaking.Bad.S01E01.1080p.WEB-DL.x264-DELUNO.1",
      name: "Breaking.Bad.S01E01.1080p.WEB-DL.x264-DELUNO.1",
      libraryId: "library-tv",
      libraryName: "TV",
      sizeBytes: 2_800_000_000,
      lastWrittenUtc: "2026-09-15T23:10:00Z",
      delunoGrabbedIt: true
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
