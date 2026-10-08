import { describe, expect, it } from "vitest";
import { heldReleaseTitle, type HeldReleaseRow } from "./held-releases";

const episode: HeldReleaseRow = {
  historyId: "h1", mediaType: "series", id: "s1", episodeId: "e1", title: "The Wire", year: 2002, episodeLabel: "S01E04", libraryId: "lib",
  releaseName: "The.Wire.S01E04.1080p.WEB", indexerName: "Nyaa", indexerId: "ix", downloadUrl: "https://x/get", quality: "WEB 1080p",
  sizeBytes: 1000, seeders: 5, reason: "Held for review.", heldUtc: "2026-10-08T00:00:00Z"
};

describe("held releases (#1159)", () => {
  it("names an episode with its code and a film with its year", () => {
    expect(heldReleaseTitle(episode)).toBe("The Wire S01E04");
    expect(heldReleaseTitle({ ...episode, mediaType: "movie", episodeId: null, episodeLabel: null, title: "Dune", year: 2021 })).toBe("Dune (2021)");
  });
});
