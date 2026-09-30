import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { ActivityEventItem } from "../../lib/api";
import { AuditTimeline } from "./audit-timeline";

const fetched: ActivityEventItem = {
  id: "e1",
  category: "download.vanished.recovered",
  label: "Found its files after it left the client",
  message: "Found Arrival's files.",
  severity: "info",
  detailsJson: null,
  relatedJobId: null,
  relatedEntityType: null,
  relatedEntityId: null,
  createdUtc: "2026-09-30T01:00:00Z"
};

/** #1086: the System timeline named events by their internal code. */
describe("System timeline names each event in plain words", () => {
  it("shows the label on fetched and live events, never the code", () => {
    render(
      <AuditTimeline
        events={[fetched]}
        liveEvents={[
          {
            id: "e2",
            message: "Waiting on the processor.",
            category: "processing.waiting",
            label: "Waiting for the processor",
            severity: "info",
            createdUtc: "2026-09-30T02:00:00Z"
          }
        ]}
      />
    );

    expect(screen.getByText("Found its files after it left the client")).toBeInTheDocument();
    expect(screen.getByText("Waiting for the processor")).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/download\.vanished|processing\.waiting/);
  });

  it("falls back to words, not the code, for a live event that arrives without a label", () => {
    render(
      <AuditTimeline
        events={[]}
        liveEvents={[
          { id: "e3", message: "Something.", category: "download.brand.new", severity: "info", createdUtc: "2026-09-30T03:00:00Z" }
        ]}
      />
    );

    expect(screen.getByText("Downloads activity")).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/download\.brand\.new/);
  });
});
