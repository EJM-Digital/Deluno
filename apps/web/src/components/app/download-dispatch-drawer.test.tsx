import { render, screen, within } from "@testing-library/react";
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

  it("lays a send out as Handoff, Journey and Trace: indexer, client, attempts, what the client detected, the import and the ids it was filed under", () => {
    const imported: DownloadDispatchItem = {
      ...failedGrab,
      status: "imported",
      failure: null,
      grabStatus: "sent",
      attemptCount: 2,
      detectedUtc: "2026-09-24T02:05:00Z",
      importStatus: "imported",
      importedFilePath: "D:\Films\Wanted Film (2011)\Wanted Film (2011).mkv",
      torrentHashOrItemId: "a779fa758f2229586eebc2e5960ee32fe5659596"
    } as DownloadDispatchItem;

    render(<DownloadDispatchDrawer dispatch={imported} onClose={() => undefined} />);

    const section = (title: string) => screen.getByText(title).closest("section") as HTMLElement;
    const handoff = within(section("Handoff"));
    expect(handoff.getByText("Fixture")).toBeVisible();
    expect(handoff.getByText("qBittorrent")).toBeVisible();
    expect(handoff.getByText("Attempts")).toBeVisible();
    expect(handoff.getByText("2")).toBeVisible();
    const journey = within(section("Journey"));
    expect(journey.getByText("Sent")).toBeVisible();
    expect(journey.getByText("Imported")).toBeVisible();
    expect(journey.getByText(/Wanted Film \(2011\)\.mkv/)).toBeVisible();
    expect(journey.queryByText("Not detected")).not.toBeInTheDocument();
    const trace = within(section("Trace"));
    expect(trace.getByText("dispatch-1")).toBeVisible();
    expect(trace.getByText("a779fa758f2229586eebc2e5960ee32fe5659596")).toBeVisible();
    expect(trace.getByText("movie · movie-1")).toBeVisible();
    expect(trace.getByText("library-1")).toBeVisible();
    expect(screen.queryByText("Why it failed")).not.toBeInTheDocument();
  });

  it("says a release chosen on Search was chosen there, not a generated id (#1062)", () => {
    render(<DownloadDispatchDrawer dispatch={{ ...failedGrab, entityType: "release", entityId: "0a1b2c3d4e5f" }} onClose={() => undefined} />);

    expect(screen.getByText("Chosen on Search")).toBeVisible();
    expect(screen.queryByText(/0a1b2c3d4e5f/)).not.toBeInTheDocument();
  });
});
