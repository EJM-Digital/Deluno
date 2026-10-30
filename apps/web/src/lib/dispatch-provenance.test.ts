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

describe("fileProvenanceFromTimeline, other recordings", () => {
  const recorded = (detailsJson: string) => [{
    id: "e", dispatchId: "d", eventType: "FILE_PROVENANCE_RECORDED", timestamp: "2026-09-15T02:00:00Z", createdUtc: "2026-09-15T02:00:00Z", detailsJson
  }];

  it("gives nothing for details that are not an object", () => {
    expect(fileProvenanceFromTimeline(recorded("null"))).toBeNull();
    expect(fileProvenanceFromTimeline(recorded("[1]"))).toBeNull();
    expect(fileProvenanceFromTimeline(recorded('"text"'))).toBeNull();
  });

  it("reads the event type in any case, and keeps a flag only when it is a real yes or no", () => {
    const result = fileProvenanceFromTimeline(recorded(JSON.stringify({ releaseNameEvidence: "Film.2026-GRP", usedFallback: "yes", rename: "  Film (2026).mkv  " })));
    expect(result).toMatchObject({ releaseNameEvidence: "Film.2026-GRP", usedFallback: null, rename: "Film (2026).mkv" });
    expect(fileProvenanceFromTimeline(recorded(JSON.stringify({ sourcePath: "E:/in/a.mkv", usedFallback: true })))?.usedFallback).toBe(true);
  });

  it("is enough to know either path, but not neither the title nor a path", () => {
    expect(fileProvenanceFromTimeline(recorded(JSON.stringify({ destinationPath: "Z:/Films/a.mkv" })))?.destinationPath).toBe("Z:/Films/a.mkv");
    expect(fileProvenanceFromTimeline(recorded(JSON.stringify({ indexerName: "Nebula" })))).toBeNull();
  });
});
