import { describe, expect, it } from "vitest";
import type { MediaStatus } from "./media-types";
import { MEDIA_STATUS_PRESENTATION, mediaStatusIsActive, wantedStatusPresentation } from "./media-status-presentation";

describe("media status presentation", () => {
  it("has a presentation for every media status", () => {
    const statuses: MediaStatus[] = ["downloaded", "downloading", "missing", "processing", "processed", "importReady", "waitingForProcessor", "importQueued", "importFailed", "imported", "processingFailed"];
    for (const status of statuses) {
      expect(MEDIA_STATUS_PRESENTATION[status]).toMatchObject({ label: expect.any(String), compactLabel: expect.any(String), tone: expect.any(String) });
    }
  });

  it("recognises active lifecycle states and handles unknown wanted values", () => {
    expect(mediaStatusIsActive("downloading")).toBe(true);
    expect(mediaStatusIsActive("processing")).toBe(true);
    expect(mediaStatusIsActive("downloaded")).toBe(false);
    expect(wantedStatusPresentation("unexpected")).toMatchObject({ label: "Tracked", tone: "muted" });
  });
});
