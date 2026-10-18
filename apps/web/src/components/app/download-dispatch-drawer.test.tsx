import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { DownloadDispatchItem } from "../../lib/api";
import { DownloadDispatchDrawer } from "./download-dispatch-drawer";

const failedGrab: DownloadDispatchItem = {
  id: "dispatch-1",
  libraryId: "library-1",
  mediaType: "movies",
  entityType: "movie",
  entityId: "movie-1",
  releaseName: "Wanted.Film.2011.1080p.WEB-DL-DELUNO",
  indexerName: "Fixture",
  downloadClientId: "client-1",
  downloadClientName: "qBittorrent",
  status: "failed",
  notesJson: null,
  createdUtc: "2026-09-24T02:00:00Z",
  grabStatus: "failed",
  grabFailureCode: "access_denied",
  failure: {
    serviceType: "download-client",
    serviceId: "client-1",
    serviceName: "qBittorrent",
    operation: "grab",
    kind: "Unknown",
    retryState: "NotRetryable",
    message: "forbidden",
    code: "access_denied",
    httpStatus: null,
    upstreamDetail: null,
    externalId: null,
    retryAfterUtc: null,
    attempts: 0,
    isTransient: false,
    legacyCategory: "access_denied",
    summary: "qBittorrent did not accept the release.",
    nextAction: "Check the download client, then search for the title again."
  }
};

describe("DownloadDispatchDrawer", () => {
  // An older build's Retry stamped nextRetryEligibleUtc with the moment it was
  // pressed. Nothing retries a failed grab on a clock, so that value is not a
  // time to show (#621).
  it("does not offer an old retry window as the next attempt on a failed grab", () => {
    const fromAnOlderBuild = { ...failedGrab, nextRetryEligibleUtc: "2026-09-24T01:00:00Z" } as DownloadDispatchItem;

    render(<DownloadDispatchDrawer dispatch={fromAnOlderBuild} onClose={() => undefined} />);

    expect(screen.getByText("Will not retry")).toBeVisible();
    expect(screen.queryByText(/Next eligible attempt/)).not.toBeInTheDocument();
    expect(screen.queryByText("Retry scheduled")).not.toBeInTheDocument();
  });

  it("still shows a retry time the failure itself carries", () => {
    render(
      <DownloadDispatchDrawer
        dispatch={{ ...failedGrab, failure: { ...failedGrab.failure!, retryState: "RetryScheduled", retryAfterUtc: "2026-09-24T03:00:00Z" } }}
        onClose={() => undefined}
      />
    );

    expect(screen.getByText("Retry scheduled")).toBeVisible();
    expect(screen.getByText(/Next eligible attempt/)).toBeVisible();
  });

  it("says a release chosen on Search was chosen there, not a generated id (#1062)", () => {
    render(<DownloadDispatchDrawer dispatch={{ ...failedGrab, entityType: "release", entityId: "0a1b2c3d4e5f" }} onClose={() => undefined} />);

    expect(screen.getByText("Chosen on Search")).toBeVisible();
    expect(screen.queryByText(/0a1b2c3d4e5f/)).not.toBeInTheDocument();
  });

  it("opens a failed send with its hand-off, journey, why it failed (retry state, the client's detail, what to do, when it can go again) and the ids to trace it", () => {
    render(
      <DownloadDispatchDrawer
        dispatch={{
          ...failedGrab,
          attemptCount: 3,
          grabAttemptedUtc: "2026-09-24T02:00:01Z",
          detectedUtc: "2026-09-24T02:05:00Z",
          torrentHashOrItemId: "torrent-hash-42",
          failure: { ...failedGrab.failure!, retryState: "RetryScheduled", retryAfterUtc: "2026-09-24T03:00:00Z", upstreamDetail: "No connection could be made because the target machine refused it.", externalId: "torrent-hash-42" }
        } as DownloadDispatchItem}
        onClose={() => undefined}
      />
    );

    for (const section of ["Handoff", "Journey", "Why it failed", "Trace"]) {
      expect(screen.getByText(section)).toBeVisible();
    }
    expect(screen.getByText("Fixture")).toBeVisible();
    expect(screen.getByText("3")).toBeVisible();
    expect(screen.getByText("Retry scheduled")).toBeVisible();
    expect(screen.getByText("qBittorrent did not accept the release.")).toBeVisible();
    expect(screen.getByText("Client detail: No connection could be made because the target machine refused it.")).toBeVisible();
    expect(screen.getByText("Check the download client, then search for the title again.")).toBeVisible();
    expect(screen.getByText(/Next eligible attempt/)).toBeVisible();
    expect(screen.getByText("dispatch-1")).toBeVisible();
    expect(screen.getAllByText("torrent-hash-42").length).toBeGreaterThan(0);
    expect(screen.getByText("library-1")).toBeVisible();
    expect(screen.queryByText("Why import failed")).not.toBeInTheDocument();
  });

  it("opens a send whose import failed with the reason the import gave, in place of why the send failed", () => {
    const { failure: _failure, ...withoutFailure } = failedGrab;
    render(
      <DownloadDispatchDrawer
        dispatch={{ ...withoutFailure, status: "imported", grabStatus: "succeeded", importStatus: "failed", importFailureMessage: "The processed file was shorter than the download." } as DownloadDispatchItem}
        onClose={() => undefined}
      />
    );

    expect(screen.getByText("Why import failed")).toBeVisible();
    expect(screen.getByText("The processed file was shorter than the download.")).toBeVisible();
    expect(screen.queryByText("Why it failed")).not.toBeInTheDocument();
    expect(screen.getByText("Trace")).toBeVisible();
  });
});
