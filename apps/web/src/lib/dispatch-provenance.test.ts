import { describe, expect, it } from "vitest";
import { fileProvenanceFromTimeline } from "./dispatch-provenance";

describe("fileProvenanceFromTimeline", () => {
  it("reads the exact pre-rename title and file journey from the newest event", () => {
    const provenance = fileProvenanceFromTimeline([
      {
        id: "event-2",
        dispatchId: "dispatch-1",
        eventType: "file_provenance_recorded",
        timestamp: "2026-09-15T02:00:00Z",
        createdUtc: "2026-09-15T02:00:00Z",
        detailsJson: JSON.stringify({
          DispatchId: "dispatch-1",
          ReleaseNameEvidence: "Movie.2026.1080p.NF.WEB-DL.x264-DELUNO",
          SourcePath: "E:\\Weir\\Ready\\Movies\\source.mkv",
          DestinationPath: "Z:\\Movies\\Movie (2026)\\Movie (2026).mkv",
          MatchedRuleNames: "Netflix, Preferred DELUNO group",
          UsedFallback: false
        })
      },
      {
        id: "event-1",
        dispatchId: "dispatch-1",
        eventType: "grab_succeeded",
        timestamp: "2026-09-15T01:00:00Z",
        createdUtc: "2026-09-15T01:00:00Z",
        detailsJson: null
      }
    ]);

    expect(provenance).toMatchObject({
      dispatchId: "dispatch-1",
      releaseNameEvidence: "Movie.2026.1080p.NF.WEB-DL.x264-DELUNO",
      matchedRuleNames: "Netflix, Preferred DELUNO group",
      usedFallback: false
    });
  });

  it("does not invent provenance from malformed or unrelated timeline data", () => {
    expect(fileProvenanceFromTimeline([])).toBeNull();
    expect(fileProvenanceFromTimeline([{
      id: "event-1",
      dispatchId: "dispatch-1",
      eventType: "file_provenance_recorded",
      timestamp: "2026-09-15T01:00:00Z",
      createdUtc: "2026-09-15T01:00:00Z",
      detailsJson: "not-json"
    }])).toBeNull();
  });
});
