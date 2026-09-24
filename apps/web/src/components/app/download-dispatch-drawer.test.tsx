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
});
