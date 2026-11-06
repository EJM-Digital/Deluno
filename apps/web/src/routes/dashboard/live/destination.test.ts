import { describe, expect, it } from "vitest";
import { buildJourney } from "./journey";
import { stageNote } from "./provenance";
import { fullSummary } from "../downloads-fixtures";

const NOW = Date.parse("2026-11-05T04:50:34Z");
const RELEASE = "Inside.Out.2.2024.1080p.x264-GRP";
const base = { queue: [], handoffs: [], dispatches: [], live: new Map(), summary: fullSummary, now: NOW };
const lib = (id: string, name: string, mediaType: string) => ({ id, name, mediaType }) as never;
const libraries = [lib("lib-kids", "Kids Movies", "movies"), lib("lib-uhd", "4K Films", "movies"), lib("lib-anime", "Anime Nights", "tv")];
const item = (libraryId: string, mediaType = "movie") => ({
  id: "abc", clientId: "qb", clientName: "qBittorrent", protocol: "torrent", mediaType, title: "Inside Out 2", releaseName: RELEASE, category: "", status: "stalled",
  progress: 100, speedMbps: 0, etaSeconds: 0, sizeBytes: 9e9, downloadedBytes: 9e9, peers: 0, indexerName: "", errorMessage: null, addedUtc: "2026-11-05T03:00:00Z",
  sourcePath: "/d/x", libraryId, healthFindings: null, handsToProcessor: false
}) as never;
const dispatch = (libraryId: string, mediaType = "movie") => ({ id: "d1", libraryId, mediaType, entityType: mediaType, entityId: "e1", releaseName: RELEASE, createdUtc: "2026-11-05T03:00:00Z" }) as never;
const note = (cards: ReturnType<typeof buildJourney>) => stageNote(cards[0]!);

describe("the importing line names the real library (#1517)", () => {
  it("uses the library's own name from the sent download", () => {
    expect(note(buildJourney({ ...base, libraries, queue: [item("lib-kids")], dispatches: [dispatch("lib-kids")] }))).toBe("Going into Kids Movies");
    expect(note(buildJourney({ ...base, libraries, queue: [item("lib-uhd")], dispatches: [dispatch("lib-uhd")] }))).toBe("Going into 4K Films");
  });
  it("uses a TV library's custom name", () => {
    expect(note(buildJourney({ ...base, libraries, queue: [item("lib-anime", "episode")], dispatches: [dispatch("lib-anime", "episode")] }))).toBe("Going into Anime Nights");
  });
  it("says the library, never a guessed name, when the library is not known", () => {
    expect(note(buildJourney({ ...base, libraries, queue: [item("lib-gone")], dispatches: [dispatch("lib-gone")] }))).toBe("Going into the library");
    expect(note(buildJourney({ ...base, queue: [item("lib-kids")], dispatches: [dispatch("lib-kids")] }))).toBe("Going into the library");
  });
});
